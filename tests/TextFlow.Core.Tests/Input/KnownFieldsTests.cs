using TextFlow.Core.Input;

namespace TextFlow.Core.Tests.Input;

public sealed class KnownFieldsTests
{
    private static readonly FieldId Editor = new(0x70CD2, -4, 0);
    private static readonly FieldId Frame = new(0x60C8A, -4, 0);
    private static readonly FieldId Password = new(0x308E4, -4, -46);

    [Fact]
    public void AFieldIsUnknown_UntilItsEvaluationAllowedCapture()
    {
        var fields = new KnownFields();

        fields.Evaluating(Editor, version: 1);
        Assert.False(fields.IsKnown(Editor));

        fields.Allowed(version: 1);
        Assert.True(fields.IsKnown(Editor));
    }

    [Fact]
    public void AStaleEvaluation_DoesNotMarkTheField()
    {
        var fields = new KnownFields();
        fields.Evaluating(Editor, version: 1);
        fields.Evaluating(Password, version: 2); // focus moved on before the first verdict

        fields.Allowed(version: 1);

        Assert.False(fields.IsKnown(Editor));
        Assert.False(fields.IsKnown(Password));
    }

    [Fact]
    public void NotepadFocusPingPong_BothFieldsBecomeKnown()
    {
        var fields = new KnownFields();
        fields.Evaluating(Frame, 1);
        fields.Allowed(1);
        fields.Evaluating(Editor, 2);
        fields.Allowed(2);

        Assert.True(fields.IsKnown(Frame));
        Assert.True(fields.IsKnown(Editor));
    }

    [Fact]
    public void ForegroundChange_ForgetsEveryField()
    {
        var fields = new KnownFields();
        fields.Evaluating(Editor, 1);
        fields.Allowed(1);

        fields.ForegroundChanged();
        fields.Allowed(1); // a verdict for the old window arriving late

        Assert.False(fields.IsKnown(Editor));
    }

    [Fact]
    public void Clear_ForgetsEveryField()
    {
        var fields = new KnownFields();
        fields.Evaluating(Editor, 1);
        fields.Allowed(1);

        fields.Clear();

        Assert.False(fields.IsKnown(Editor));
    }

    [Fact]
    public void TheSetIsBounded_OldestFieldsGoFirst()
    {
        var fields = new KnownFields();
        for (var i = 0; i < KnownFields.Capacity + 5; i++)
        {
            fields.Evaluating(new FieldId(0x1000, -4, -i), i);
            fields.Allowed(i);
        }

        Assert.False(fields.IsKnown(new FieldId(0x1000, -4, 0)));
        Assert.True(fields.IsKnown(new FieldId(0x1000, -4, -(KnownFields.Capacity + 4))));
    }
}
