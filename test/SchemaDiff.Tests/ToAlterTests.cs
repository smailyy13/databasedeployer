using SchemaDiff.Core.Scripting;

namespace SchemaDiff.Tests;

public class ToAlterTests
{
    [Fact]
    public void Converts_leading_create_to_alter()
    {
        var result = ModuleScriptGenerator.ToAlter("CREATE VIEW dbo.v AS SELECT 1 AS x", quotedIdentifiers: true);
        Assert.NotNull(result);
        Assert.StartsWith("ALTER VIEW", result);
    }

    [Fact]
    public void Preserves_body_after_the_verb()
    {
        var result = ModuleScriptGenerator.ToAlter(
            "CREATE PROCEDURE dbo.p AS BEGIN SELECT 1 END", quotedIdentifiers: true);
        Assert.Equal("ALTER PROCEDURE dbo.p AS BEGIN SELECT 1 END", result);
    }

    [Fact]
    public void Does_not_touch_the_word_create_inside_a_comment()
    {
        // Token tabanlı dönüşümün asıl gerekçesi: yorumdaki "create" bozulmamalı.
        var input = "/* bu create ifadesini oluşturur */\nCREATE VIEW dbo.v AS SELECT 1 AS x";
        var result = ModuleScriptGenerator.ToAlter(input, quotedIdentifiers: true);
        Assert.NotNull(result);
        Assert.Contains("bu create ifadesini", result);
        Assert.Contains("ALTER VIEW", result);
        Assert.DoesNotContain("ALTER ifadesini", result);
    }

    [Fact]
    public void Handles_leading_whitespace_before_verb()
    {
        var result = ModuleScriptGenerator.ToAlter("   \n  CREATE FUNCTION dbo.f() RETURNS int AS BEGIN RETURN 1 END",
            quotedIdentifiers: true);
        Assert.NotNull(result);
        Assert.Contains("ALTER FUNCTION", result);
    }

    [Fact]
    public void Returns_null_when_definition_cannot_be_parsed()
    {
        var result = ModuleScriptGenerator.ToAlter("this is ''' not parseable", quotedIdentifiers: true);
        Assert.Null(result);
    }
}
