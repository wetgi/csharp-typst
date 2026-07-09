using Xunit;

namespace TypstRender.Client.Tests;

public sealed class TemplateScannerTests : IDisposable
{
    private readonly string _root;

    public TemplateScannerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "typst-scanner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void WriteFile(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    [Fact]
    public void Auto_IncludesEntrySubtreeAndImportClosure_ExcludesSiblingTemplates()
    {
        WriteFile("shared/styles.typ", "#let primary = rgb(\"#102030\")");
        WriteFile("invoice/main.typ", "#import \"/shared/styles.typ\": primary\n#import \"parts.typ\": x");
        WriteFile("invoice/parts.typ", "#let x = 1");
        WriteFile("invoice/data.json", "{}");
        WriteFile("letter/main.typ", "= Letter");

        var result = TemplateScanner.Scan(_root, "invoice/main.typ", BundleMode.Auto);

        Assert.Null(result.FullFolderReason);
        Assert.Equal(
            ["invoice/data.json", "invoice/main.typ", "invoice/parts.typ", "shared/styles.typ"],
            result.Files);
    }

    [Fact]
    public void Auto_FollowsTransitiveImportsAndRelativeAssetsOfSharedModules()
    {
        WriteFile("shared/styles.typ", "#import \"palette.typ\": c\n#let watermark = image(\"watermark.png\")");
        WriteFile("shared/palette.typ", "#let c = blue");
        WriteFile("shared/watermark.png", "png");
        WriteFile("invoice/main.typ", "#import \"/shared/styles.typ\": watermark");

        var result = TemplateScanner.Scan(_root, "invoice/main.typ", BundleMode.Auto);

        Assert.Equal(
            ["invoice/main.typ", "shared/palette.typ", "shared/styles.typ", "shared/watermark.png"],
            result.Files);
    }

    [Fact]
    public void Auto_AlwaysIncludesFontsDirectory()
    {
        WriteFile("fonts/Custom.ttf", "ttf");
        WriteFile("invoice/main.typ", "= Hi");
        WriteFile("letter/main.typ", "= Letter");

        var result = TemplateScanner.Scan(_root, "invoice/main.typ", BundleMode.Auto);

        Assert.Equal(["fonts/Custom.ttf", "invoice/main.typ"], result.Files);
    }

    [Fact]
    public void Auto_MissingLiteralImport_ThrowsWithReferenceChain()
    {
        WriteFile("invoice/main.typ", "#import \"/shared/missing.typ\": x");

        var ex = Assert.Throws<FileNotFoundException>(
            () => TemplateScanner.Scan(_root, "invoice/main.typ", BundleMode.Auto));

        Assert.Contains("invoice/main.typ", ex.Message);
        Assert.Contains("shared/missing.typ", ex.Message);
    }

    [Fact]
    public void Auto_DynamicImportExpression_FallsBackToFullFolder()
    {
        WriteFile("invoice/main.typ", "#let mod = \"parts.typ\"\n#import mod: x");
        WriteFile("invoice/parts.typ", "#let x = 1");
        WriteFile("letter/main.typ", "= Letter");

        var result = TemplateScanner.Scan(_root, "invoice/main.typ", BundleMode.Auto);

        Assert.NotNull(result.FullFolderReason);
        Assert.Contains("invoice/main.typ", result.FullFolderReason);
        Assert.Contains("letter/main.typ", result.Files);
    }

    [Fact]
    public void Auto_IgnoresPackageImportsCommentsAndDynamicReaders()
    {
        // json(data-path) is the data convention; "@preview/..." is a package;
        // the commented-out import must not pull files in or trip the dynamic check.
        WriteFile("invoice/main.typ",
            "#import \"@preview/cetz:0.2.2\"\n" +
            "// #import nope: x\n" +
            "/* #import \"/shared/gone.typ\": y */\n" +
            "#let data-path = sys.inputs.at(\"data-path\", default: \"data.json\")\n" +
            "#let inv = json(data-path)");

        var result = TemplateScanner.Scan(_root, "invoice/main.typ", BundleMode.Auto);

        Assert.Null(result.FullFolderReason);
        Assert.Equal(["invoice/main.typ"], result.Files);
    }

    [Fact]
    public void Auto_SkipsConventionalDataFileReference()
    {
        // /data.json is injected by the client at render time and never on disk.
        WriteFile("invoice/main.typ", "#let inv = json(\"/data.json\")");

        var result = TemplateScanner.Scan(_root, "invoice/main.typ", BundleMode.Auto);

        Assert.Equal(["invoice/main.typ"], result.Files);
    }

    [Fact]
    public void Auto_DeclaredExtraPath_IsToleratedAndNotExpectedOnDisk()
    {
        // /generated/chart.svg is injected by the client at render time
        // (TypstRenderRequest.ExtraFiles), like /data.json.
        WriteFile("report/main.typ", "#image(\"/generated/chart.svg\")");

        var result = TemplateScanner.Scan(
            _root, "report/main.typ", BundleMode.Auto, ["generated/chart.svg"]);

        Assert.Null(result.FullFolderReason);
        Assert.Equal(["report/main.typ"], result.Files);
    }

    [Fact]
    public void Auto_UndeclaredMissingAssetReference_StillThrows()
    {
        WriteFile("report/main.typ", "#image(\"/generated/chart.svg\")");

        Assert.Throws<FileNotFoundException>(
            () => TemplateScanner.Scan(_root, "report/main.typ", BundleMode.Auto));
    }

    [Fact]
    public void Auto_ReferenceEscapingRoot_Throws()
    {
        WriteFile("invoice/main.typ", "#import \"../../outside.typ\": x");

        Assert.Throws<InvalidOperationException>(
            () => TemplateScanner.Scan(_root, "invoice/main.typ", BundleMode.Auto));
    }

    [Fact]
    public void Auto_EntryAtRoot_BundlesWholeRoot()
    {
        WriteFile("main.typ", "= Hi");
        WriteFile("extra/img.png", "png");

        var result = TemplateScanner.Scan(_root, "main.typ", BundleMode.Auto);

        Assert.Equal(["extra/img.png", "main.typ"], result.Files);
    }

    [Fact]
    public void Full_BundlesEverything()
    {
        WriteFile("invoice/main.typ", "= Hi");
        WriteFile("letter/main.typ", "= Letter");

        var result = TemplateScanner.Scan(_root, "invoice/main.typ", BundleMode.Full);

        Assert.Equal(["invoice/main.typ", "letter/main.typ"], result.Files);
    }

    [Fact]
    public void MissingEntry_Throws()
    {
        WriteFile("invoice/main.typ", "= Hi");

        Assert.Throws<FileNotFoundException>(
            () => TemplateScanner.Scan(_root, "letter/main.typ", BundleMode.Auto));
    }
}
