namespace TypstRender.Sample;

public sealed class TemplateNotFoundException(string templateName, IReadOnlyList<string> availableTemplates) : Exception($"Unknown template '{templateName}'.")
{

    public string TemplateName { get; } = templateName;

    public IReadOnlyList<string> AvailableTemplates { get; } = availableTemplates.ToArray();
}
