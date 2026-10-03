using Typedown.Automation;

namespace Typedown.Uno.Automation;

/// <summary>
/// The one edit coordinator of the application: the automation methods and the application's own whole-document
/// edits (File > Upload local images) take their turns on a document through it, so neither overwrites the other and
/// each is one undo step. The same as the Windows edition's DocumentEdits.
/// </summary>
public static class DocumentEdits
{
    public static DocumentEditCoordinator Coordinator { get; } = new(UnoAutomationHost.EditorClassifierVersion);
}
