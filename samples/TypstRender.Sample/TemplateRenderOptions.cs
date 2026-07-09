namespace TypstRender.Sample;

public sealed record TemplateRenderOptions(string TemplateRoot)
{
    public const string TemplatesDirectoryName = "templates";
    public const string EntryFile = "main.typ";
    public const string DataFile = "data.json";
}
