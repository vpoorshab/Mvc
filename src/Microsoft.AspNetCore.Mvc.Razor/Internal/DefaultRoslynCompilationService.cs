// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Razor.Compilation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.AspNetCore.Mvc.Razor.Internal
{
    /// <summary>
    /// A type that uses Roslyn to compile C# content.
    /// </summary>
    public class DefaultRoslynCompilationService : ICompilationService
    {
        private readonly DebugInformationFormat _pdbFormat =
#if NET451
            SymbolsUtility.SupportsFullPdbGeneration() ?
                DebugInformationFormat.Pdb :
                DebugInformationFormat.PortablePdb;
#else
            DebugInformationFormat.PortablePdb;
#endif
        private readonly ApplicationPartManager _partManager;
        private readonly IFileProvider _fileProvider;
        private readonly Action<RoslynCompilationContext> _compilationCallback;
        private readonly CSharpParseOptions _parseOptions;
        private readonly CSharpCompilationOptions _compilationOptions;
        private readonly IList<MetadataReference> _additionalMetadataReferences;
        private readonly ILogger _logger;
        private object _compilationReferencesLock = new object();
        private bool _compilationReferencesInitialized;
        private IList<MetadataReference> _compilationReferences;

        /// <summary>
        /// Initalizes a new instance of the <see cref="DefaultRoslynCompilationService"/> class.
        /// </summary>
        /// <param name="partManager">The <see cref="ApplicationPartManager"/>.</param>
        /// <param name="optionsAccessor">Accessor to <see cref="RazorViewEngineOptions"/>.</param>
        /// <param name="fileProviderAccessor">The <see cref="IRazorViewEngineFileProviderAccessor"/>.</param>
        /// <param name="loggerFactory">The <see cref="ILoggerFactory"/>.</param>
        public DefaultRoslynCompilationService(
            ApplicationPartManager partManager,
            IOptions<RazorViewEngineOptions> optionsAccessor,
            IRazorViewEngineFileProviderAccessor fileProviderAccessor,
            ILoggerFactory loggerFactory)
        {
            _partManager = partManager;
            _fileProvider = fileProviderAccessor.FileProvider;
            _compilationCallback = optionsAccessor.Value.CompilationCallback;
            _parseOptions = optionsAccessor.Value.ParseOptions;
            _compilationOptions = optionsAccessor.Value.CompilationOptions;
            _additionalMetadataReferences = optionsAccessor.Value.AdditionalCompilationReferences;
            _logger = loggerFactory.CreateLogger<DefaultRoslynCompilationService>();
        }

        private IList<MetadataReference> CompilationReferences
        {
            get
            {
                return LazyInitializer.EnsureInitialized(
                    ref _compilationReferences,
                    ref _compilationReferencesInitialized,
                    ref _compilationReferencesLock,
                    GetCompilationReferences);
            }
        }

        /// <inheritdoc />
        public CompilationResult Compile(RelativeFileInfo fileInfo, string compilationContent)
        {
            if (fileInfo == null)
            {
                throw new ArgumentNullException(nameof(fileInfo));
            }

            if (compilationContent == null)
            {
                throw new ArgumentNullException(nameof(compilationContent));
            }

            _logger.GeneratedCodeToAssemblyCompilationStart(fileInfo.RelativePath);

            var startTimestamp = _logger.IsEnabled(LogLevel.Debug) ? Stopwatch.GetTimestamp() : 0;

            var assemblyName = Path.GetRandomFileName();

            var sourceText = SourceText.From(compilationContent, Encoding.UTF8);
            var syntaxTree = CSharpSyntaxTree.ParseText(
                sourceText,
                path: assemblyName,
                options: _parseOptions);

            var compilation = CSharpCompilation.Create(
                assemblyName,
                options: _compilationOptions,
                syntaxTrees: new[] { syntaxTree },
                references: CompilationReferences);

            compilation = Rewrite(compilation);

            var compilationContext = new RoslynCompilationContext(compilation);
            _compilationCallback(compilationContext);
            compilation = compilationContext.Compilation;

            using (var assemblyStream = new MemoryStream())
            {
                using (var pdbStream = new MemoryStream())
                {
                    var result = compilation.Emit(
                        assemblyStream,
                        pdbStream,
                        options: new EmitOptions(debugInformationFormat: _pdbFormat));

                    if (!result.Success)
                    {
                        if (!compilation.References.Any() && !CompilationReferences.Any())
                        {
                            // DependencyModel had no references specified and the user did not use the
                            // CompilationCallback to add extra references. It is likely that the user did not specify
                            // preserveCompilationContext in the app's project file.
                            throw new InvalidOperationException(
                                Resources.FormatCompilation_DependencyContextIsNotSpecified(
                                    fileInfo.RelativePath,
                                    "Microsoft.NET.Sdk.Web",
                                    "PreserveCompilationContext"));
                        }

                        return GetCompilationFailedResult(
                            fileInfo.RelativePath,
                            compilationContent,
                            assemblyName,
                            result.Diagnostics);
                    }

                    assemblyStream.Seek(0, SeekOrigin.Begin);
                    pdbStream.Seek(0, SeekOrigin.Begin);

                    var assembly = LoadStream(assemblyStream, pdbStream);
                    var type = assembly.GetExportedTypes().FirstOrDefault(a => !a.IsNested);
                    _logger.GeneratedCodeToAssemblyCompilationEnd(fileInfo.RelativePath, startTimestamp);

                    return new CompilationResult(type);
                }
            }
        }

        /// <summary>
        /// Gets the sequence of <see cref="MetadataReference"/> instances used for compilation.
        /// </summary>
        /// <returns>The <see cref="MetadataReference"/> instances.</returns>
        protected virtual IList<MetadataReference> GetCompilationReferences()
        {
            var feature = new MetadataReferenceFeature();
            _partManager.PopulateFeature(feature);
            var applicationReferences = feature.MetadataReferences;

            if (_additionalMetadataReferences.Count == 0)
            {
                return applicationReferences;
            }

            var compilationReferences = new List<MetadataReference>(applicationReferences.Count + _additionalMetadataReferences.Count);
            compilationReferences.AddRange(applicationReferences);
            compilationReferences.AddRange(_additionalMetadataReferences);

            return compilationReferences;
        }

        private Assembly LoadStream(MemoryStream assemblyStream, MemoryStream pdbStream)
        {
#if NET451
            return Assembly.Load(assemblyStream.ToArray(), pdbStream.ToArray());
#else
            return System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromStream(assemblyStream, pdbStream);
#endif
        }

        private CSharpCompilation Rewrite(CSharpCompilation compilation)
        {
            var rewrittenTrees = new List<SyntaxTree>();
            foreach (var tree in compilation.SyntaxTrees)
            {
                var semanticModel = compilation.GetSemanticModel(tree, ignoreAccessibility: true);
                var rewriter = new ExpressionRewriter(semanticModel);

                var rewrittenTree = tree.WithRootAndOptions(rewriter.Visit(tree.GetRoot()), tree.Options);
                rewrittenTrees.Add(rewrittenTree);
            }

            return compilation.RemoveAllSyntaxTrees().AddSyntaxTrees(rewrittenTrees);
        }

        // Internal for unit testing
        internal CompilationResult GetCompilationFailedResult(
            string relativePath,
            string compilationContent,
            string assemblyName,
            IEnumerable<Diagnostic> diagnostics)
        {
            var diagnosticGroups = diagnostics
                .Where(IsError)
                .GroupBy(diagnostic => GetFilePath(relativePath, diagnostic), StringComparer.Ordinal);

            var failures = new List<CompilationFailure>();
            foreach (var group in diagnosticGroups)
            {
                var sourceFilePath = group.Key;
                string sourceFileContent;
                if (string.Equals(assemblyName, sourceFilePath, StringComparison.Ordinal))
                {
                    // The error is in the generated code and does not have a mapping line pragma
                    sourceFileContent = compilationContent;
                    sourceFilePath = Resources.GeneratedCodeFileName;
                }
                else
                {
                    sourceFileContent = ReadFileContentsSafely(_fileProvider, sourceFilePath);
                }

                var compilationFailure = new CompilationFailure(
                    sourceFilePath,
                    sourceFileContent,
                    compilationContent,
                    group.Select(GetDiagnosticMessage));

                failures.Add(compilationFailure);
            }

            return new CompilationResult(failures);
        }

        private static string GetFilePath(string relativePath, Diagnostic diagnostic)
        {
            if (diagnostic.Location == Location.None)
            {
                return relativePath;
            }

            return diagnostic.Location.GetMappedLineSpan().Path;
        }

        private static bool IsError(Diagnostic diagnostic)
        {
            return diagnostic.IsWarningAsError || diagnostic.Severity == DiagnosticSeverity.Error;
        }

        private static string ReadFileContentsSafely(IFileProvider fileProvider, string filePath)
        {
            var fileInfo = fileProvider.GetFileInfo(filePath);
            if (fileInfo.Exists)
            {
                try
                {
                    using (var reader = new StreamReader(fileInfo.CreateReadStream()))
                    {
                        return reader.ReadToEnd();
                    }
                }
                catch
                {
                    // Ignore any failures
                }
            }

            return null;
        }

        private static DiagnosticMessage GetDiagnosticMessage(Diagnostic diagnostic)
        {
            var mappedLineSpan = diagnostic.Location.GetMappedLineSpan();
            return new DiagnosticMessage(
                diagnostic.GetMessage(),
                CSharpDiagnosticFormatter.Instance.Format(diagnostic),
                mappedLineSpan.Path,
                mappedLineSpan.StartLinePosition.Line + 1,
                mappedLineSpan.StartLinePosition.Character + 1,
                mappedLineSpan.EndLinePosition.Line + 1,
                mappedLineSpan.EndLinePosition.Character + 1);
        }
    }
}
