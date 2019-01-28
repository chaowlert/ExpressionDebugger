﻿using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;

namespace ExpressionDebugger
{
    public class ExpressionCompiler
    {
        private readonly ExpressionCompilationOptions _options;
        public ExpressionCompiler(ExpressionCompilationOptions options)
        {
            _options = options;
        }

        private readonly List<SyntaxTree> _codes = new List<SyntaxTree>();
        public void AddFile(string code, string filename)
        {
            var buffer = Encoding.UTF8.GetBytes(code);

            var path = _options?.RootPath == null ? filename : Path.Combine(_options.RootPath, filename);
            if (_options?.EmitFile == true)
            {
                using (var fs = new FileStream(path, FileMode.Create))
                {
                    fs.Write(buffer, 0, buffer.Length);
                }
            }

            var sourceText = SourceText.From(buffer, buffer.Length, Encoding.UTF8, canBeEmbedded: true);

            var syntaxTree = CSharpSyntaxTree.ParseText(
                sourceText,
                new CSharpParseOptions(),
                path);

            var syntaxRootNode = syntaxTree.GetRoot() as CSharpSyntaxNode;
            var encoded = CSharpSyntaxTree.Create(syntaxRootNode, null, path, Encoding.UTF8);
            _codes.Add(encoded);
        }

        public Assembly CreateAssembly(IEnumerable<Assembly> assemblies)
        {
            var assemblyName = Path.GetRandomFileName();
            var symbolsName = Path.ChangeExtension(assemblyName, "pdb");

            var references = assemblies.Select(it => MetadataReference.CreateFromFile(it.Location));
            CSharpCompilation compilation = CSharpCompilation.Create(
                assemblyName,
                _codes,
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, usings: new[] { "System" } )
                    .WithOptimizationLevel(_options?.IsRelease == true ? OptimizationLevel.Release : OptimizationLevel.Debug)
                    .WithPlatform(Platform.AnyCpu)
            );

            using (var assemblyStream = new MemoryStream())
            using (var symbolsStream = new MemoryStream())
            {
                var emitOptions = new EmitOptions(
                        debugInformationFormat: DebugInformationFormat.PortablePdb,
                        pdbFilePath: symbolsName);

                var embeddedTexts = _codes.Select(it => EmbeddedText.FromSource(it.FilePath, it.GetText()));

                EmitResult result = compilation.Emit(
                    peStream: assemblyStream,
                    pdbStream: symbolsStream,
                    embeddedTexts: embeddedTexts,
                    options: emitOptions);

                if (!result.Success)
                {
                    var errors = new List<string>();

                    IEnumerable<Diagnostic> failures = result.Diagnostics.Where(diagnostic =>
                        diagnostic.IsWarningAsError ||
                        diagnostic.Severity == DiagnosticSeverity.Error);

                    foreach (Diagnostic diagnostic in failures)
                        errors.Add($"{diagnostic.Id}: {diagnostic.GetMessage()}");

                    throw new Exception(string.Join("\n", errors));
                }

                assemblyStream.Seek(0, SeekOrigin.Begin);
                symbolsStream?.Seek(0, SeekOrigin.Begin);

                return AssemblyLoadContext.Default.LoadFromStream(assemblyStream, symbolsStream);
            }
        }

    }
}