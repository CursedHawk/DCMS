namespace Dcms.Plugins.DynamicApps.Automation;

/// <summary>
/// How flows are written, in words a model reads: the assistant's GetFlowLanguage and the flow
/// editor's "Write with AI" both teach from these, so the two cannot describe different languages.
/// </summary>
public static class FlowLanguageText
{
    public static readonly string Expressions =
        "Conditions and {{ }} holes: literals (numbers, 'text' or \"text\", true, false, null, [lists]), names, member (row.amount) "
        + "and index (row['x'], list[0]) access, + - * / %, == != < <= > >=, && || !, `x in list` (or substring in text), and "
        + "test ? a : b. No loops, no assignment, no other functions. A missing member is null rather than an error. "
        + $"At most {Automation.Expressions.MaxSourceChars} characters.";

    public const string Templates =
        "Strings in a step's input may hold {{ expression }} holes. A string that is exactly one hole becomes the expression's value "
        + "(a number, list or object stays one); otherwise each hole renders as text. In dcms.email.send's html every value is "
        + "HTML-encoded. A script step's code is never a template.";

    public const string Scripts =
        "script.run@1 input: { language: \"ts\" | \"js\", code, args }. code is a module whose default export is "
        + "main(ctx: FlowContext), sync or async; it returns the step's output (any JSON). ctx holds event, row, previous, "
        + "changedFields, input, steps (earlier steps' outputs), run, and args (this step's args, templates rendered). Pure: no "
        + "network, files, environment or imports; 2 s, a 64 MB heap, 64,000 characters of code and of output. Pass values in "
        + "through args — code is copied as written.";
}
