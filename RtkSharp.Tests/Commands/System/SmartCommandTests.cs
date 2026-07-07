using RtkSharp.Commands.System;
using RtkSharp.Core;
using Xunit;

namespace RtkSharp.Tests.Commands.System;

/// <summary>
/// Test-for-test port of Rust <c>src/cmds/system/local_llm.rs</c>'s <c>#[cfg(test)] mod tests</c>
/// (<c>test_rust_analysis</c>, <c>test_python_analysis</c>), plus additional coverage for the
/// JavaScript/TypeScript and Go extraction/pattern-detection branches the Rust module also
/// implements but doesn't exercise with its own inline tests.
/// </summary>
public sealed class SmartCommandTests
{
    // ===================== Ported from local_llm.rs's #[cfg(test)] mod tests =====================

    [Fact]
    public void AnalyzeCode_RustAnalysis_MatchesRustFixture()
    {
        // Verbatim port of local_llm.rs's test_rust_analysis fixture and assertions.
        const string code = """

            use anyhow::Result;
            use std::fs;

            pub struct Config {
                name: String,
            }

            pub fn load_config() -> Result<Config> {
                Ok(Config { name: "test".into() })
            }

            fn helper() {}

            """;

        var summary = SmartCommand.AnalyzeCode(code, Language.Rust);

        Assert.Contains("Rust", summary.Line1, StringComparison.Ordinal);
        Assert.Contains("fn", summary.Line1, StringComparison.Ordinal);
    }

    [Fact]
    public void AnalyzeCode_PythonAnalysis_MatchesRustFixture()
    {
        // Verbatim port of local_llm.rs's test_python_analysis fixture and assertions.
        const string code = """

            import json
            from pathlib import Path

            class Config:
                def __init__(self, name):
                    self.name = name

            def load_config():
                return Config("test")

            """;

        var summary = SmartCommand.AnalyzeCode(code, Language.Python);

        Assert.Contains("Python", summary.Line1, StringComparison.Ordinal);
    }

    // ===================== Additional coverage: Rust extraction/pattern details =====================

    [Fact]
    public void AnalyzeCode_RustAnalysis_DetailsLineReportsImportsAndErrorHandling()
    {
        const string code = """

            use anyhow::Result;
            use std::fs;

            pub struct Config {
                name: String,
            }

            pub fn load_config() -> Result<Config> {
                Ok(Config { name: "test".into() })
            }

            fn helper() {}

            """;

        var summary = SmartCommand.AnalyzeCode(code, Language.Rust);

        // "std" is filtered as a stdlib import (is_std_import); "anyhow" survives.
        Assert.Contains("uses: anyhow", summary.Line2, StringComparison.Ordinal);
        Assert.Contains("patterns: error handling", summary.Line2, StringComparison.Ordinal);
        Assert.Contains("2 fn", summary.Line1, StringComparison.Ordinal);
        Assert.Contains("1 struct", summary.Line1, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractImports_Rust_FiltersStdCoreAllocAndDedupes()
    {
        const string code = """
            use std::fs;
            use core::mem;
            use alloc::vec::Vec;
            use anyhow::Result;
            use anyhow::Context;
            use serde::Serialize;
            """;

        var imports = SmartCommand.ExtractImports(code, Language.Rust);

        Assert.Equal(["anyhow", "serde"], imports);
    }

    [Fact]
    public void ExtractFunctions_Rust_ExcludesTestMainAndNew()
    {
        const string code = """
            pub fn alpha() {}
            fn test_helper() {}
            fn main() {}
            fn new() -> Self { Self {} }
            async fn beta() {}
            """;

        var functions = SmartCommand.ExtractFunctions(code, Language.Rust);

        Assert.Equal(["alpha", "beta"], functions);
    }

    [Fact]
    public void DetectPatterns_Rust_TruncatesToFirstThreeInSourceOrder()
    {
        // Rust's Rust-language arm checks patterns in a fixed order: trait-impl, derive,
        // error-handling, tests, dyn-dispatch (local_llm.rs:238-253) — then the whole list is
        // take(3)'d. This fixture matches all of trait-impl/derive/tests/dyn-dispatch but NOT
        // error-handling, so the 4th matching pattern in source order (dyn dispatch) is the one
        // truncated away, not "tests" (which appears earlier in the fixed check order).
        const string code = """
            #[derive(Debug, Clone)]
            pub struct Foo;

            impl Bar for Foo {}

            fn get() -> Box<dyn Bar> { todo!() }

            #[test]
            fn it_works() {}
            """;

        var patterns = SmartCommand.DetectPatterns(code, Language.Rust);

        Assert.Equal(["trait impl", "derive", "tests"], patterns);
    }

    [Fact]
    public void DetectPatterns_Rust_DetectsAsyncAcrossContent()
    {
        const string code = "pub async fn fetch() { let x = something().await; }";

        var patterns = SmartCommand.DetectPatterns(code, Language.Rust);

        Assert.Contains("async", patterns);
    }

    // ===================== Additional coverage: Python =====================

    [Fact]
    public void ExtractImports_Python_FiltersStdlibAndKeepsFromImport()
    {
        const string code = """
            import json
            import os
            from pathlib import Path
            from requests import get
            """;

        var imports = SmartCommand.ExtractImports(code, Language.Python);

        // "json"/"os" are stdlib and filtered; "pathlib"/"requests" survive.
        Assert.Equal(["pathlib", "requests"], imports);
    }

    [Fact]
    public void DetectPatterns_Python_DetectsDataclassAndOop()
    {
        const string code = """
            @dataclass
            class Point:
                x: int
                y: int

            class Widget:
                def __init__(self, name):
                    self.name = name
            """;

        var patterns = SmartCommand.DetectPatterns(code, Language.Python);

        Assert.Contains("dataclass", patterns);
        Assert.Contains("OOP", patterns);
    }

    [Fact]
    public void ExtractStructs_Python_ExtractsClassNames()
    {
        const string code = """
            class Alpha:
                pass

            class Beta(Alpha):
                pass
            """;

        var structs = SmartCommand.ExtractStructs(code, Language.Python);

        Assert.Equal(["Alpha", "Beta"], structs);
    }

    // ===================== Additional coverage: JavaScript / TypeScript =====================

    [Fact]
    public void ExtractImports_JavaScript_ExtractsFromImportAndRequire()
    {
        const string code = """
            import React from 'react';
            import { useState } from "react";
            const fs = require('fs');
            """;

        var imports = SmartCommand.ExtractImports(code, Language.JavaScript);

        Assert.Equal(["react", "fs"], imports);
    }

    [Fact]
    public void ExtractFunctions_JavaScript_ExtractsFunctionAndArrowDeclarations()
    {
        const string code = """
            function alpha() {}
            async function beta() {}
            const gamma = (x) => x + 1;
            const delta = async (x) => x + 1;
            let epsilon = (x) => x;
            """;

        var functions = SmartCommand.ExtractFunctions(code, Language.JavaScript);

        Assert.Equal(["alpha", "beta", "gamma", "delta", "epsilon"], functions);
    }

    [Fact]
    public void DetectPatterns_JavaScript_DetectsReactHooksAndEsModules()
    {
        const string code = """
            import { useState, useEffect } from 'react';

            function Widget() {
                const [count, setCount] = useState(0);
                useEffect(() => {}, []);
                return null;
            }

            export default Widget;
            """;

        var patterns = SmartCommand.DetectPatterns(code, Language.JavaScript);

        Assert.Contains("React hooks", patterns);
        Assert.Contains("ES modules", patterns);
    }

    [Fact]
    public void ExtractStructs_TypeScript_ExtractsInterfaceClassAndTypeDeclarations()
    {
        const string code = """
            interface Point { x: number; y: number; }
            class Widget implements Point { x = 0; y = 0; }
            type Id = string;
            """;

        var structs = SmartCommand.ExtractStructs(code, Language.TypeScript);

        Assert.Equal(["Point", "Widget", "Id"], structs);
    }

    [Fact]
    public void ExtractTraits_TypeScript_ExtractsInterfaceNames()
    {
        const string code = """
            interface Point { x: number; }
            interface Named { name: string; }
            """;

        var traits = SmartCommand.ExtractTraits(code, Language.TypeScript);

        Assert.Equal(["Point", "Named"], traits);
    }

    [Fact]
    public void AnalyzeCode_TypeScriptModule_ReportsInterfaceAsTrait()
    {
        const string code = """
            import { useState } from 'react';

            export interface Props {
                label: string;
            }

            export function Widget(props: Props) {
                const [value] = useState(props.label);
                return value;
            }
            """;

        var summary = SmartCommand.AnalyzeCode(code, Language.TypeScript);

        Assert.Contains("TypeScript", summary.Line1, StringComparison.Ordinal);
        Assert.Contains("1 trait", summary.Line1, StringComparison.Ordinal);
    }

    // ===================== Additional coverage: Go =====================

    [Fact]
    public void ExtractFunctions_Go_ExtractsPlainAndMethodFunctions()
    {
        const string code = """
            func Alpha() {}
            func (s *Server) Beta() {}
            """;

        var functions = SmartCommand.ExtractFunctions(code, Language.Go);

        Assert.Equal(["Alpha", "Beta"], functions);
    }

    [Fact]
    public void ExtractStructs_Go_ExtractsStructTypeDeclarations()
    {
        const string code = """
            type Server struct {
                Addr string
            }

            type Handler struct{}
            """;

        var structs = SmartCommand.ExtractStructs(code, Language.Go);

        Assert.Equal(["Server", "Handler"], structs);
    }

    [Fact]
    public void ExtractImports_Go_ExtractsQuotedImportPaths()
    {
        const string code = """
            import (
                "fmt"
                "net/http"
            )
            """;

        var imports = SmartCommand.ExtractImports(code, Language.Go);

        Assert.Equal(["fmt", "net/http"], imports);
    }

    // ===================== LangDisplayName / fallback coverage =====================

    [Theory]
    [InlineData(Language.Rust, "Rust")]
    [InlineData(Language.Python, "Python")]
    [InlineData(Language.JavaScript, "JavaScript")]
    [InlineData(Language.TypeScript, "TypeScript")]
    [InlineData(Language.Go, "Go")]
    [InlineData(Language.C, "C")]
    [InlineData(Language.Cpp, "C++")]
    [InlineData(Language.Java, "Java")]
    [InlineData(Language.Ruby, "Ruby")]
    [InlineData(Language.Shell, "Shell")]
    [InlineData(Language.Data, "Data")]
    [InlineData(Language.Unknown, "Code")]
    public void LangDisplayName_MatchesRustMapping(Language lang, string expected)
    {
        Assert.Equal(expected, SmartCommand.LangDisplayName(lang));
    }

    [Fact]
    public void AnalyzeCode_EmptyContent_ReturnsGeneralPurposeFallback()
    {
        var summary = SmartCommand.AnalyzeCode(string.Empty, Language.Unknown);

        Assert.Equal("Code code (0 lines)", summary.Line1);
        Assert.Equal("General purpose code file", summary.Line2);
    }
}
