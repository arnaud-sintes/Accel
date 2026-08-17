namespace Accel.Tests;

using Accel.App.Services;
using Xunit;

/// <summary>Unit tests for <see cref="CommonCliFlags"/> - the permission-mode combo-to-argv translation
/// shared by the Create-session and Edit-launch-args dialogs.</summary>
public class CommonCliFlagsTests
{
    [Fact]
    public void BuildArguments_DefaultMode_IsEmpty()
    {
        var arguments = CommonCliFlags.BuildArguments(PermissionModeOption.None);
        Assert.Empty(arguments);
    }

    [Theory]
    [InlineData(PermissionModeOption.AcceptEdits, "acceptEdits")]
    [InlineData(PermissionModeOption.BypassPermissions, "bypassPermissions")]
    [InlineData(PermissionModeOption.Plan, "plan")]
    public void BuildArguments_NonNoneMode_EmitsPermissionModeFlagAsTwoElements(PermissionModeOption mode, string expectedValue)
    {
        var arguments = CommonCliFlags.BuildArguments(mode);

        Assert.Equal(new[] { "--permission-mode", expectedValue }, arguments);
    }

    [Fact]
    public void ToFlagValue_None_IsNull()
    {
        Assert.Null(CommonCliFlags.ToFlagValue(PermissionModeOption.None));
    }

    [Fact]
    public void PermissionModeChoices_IncludesEveryEnumValueExactlyOnce()
    {
        var values = new System.Collections.Generic.List<PermissionModeOption>();
        foreach (var choice in CommonCliFlags.PermissionModeChoices)
        {
            values.Add(choice.Value);
        }

        Assert.Equal(
            new[]
            {
                PermissionModeOption.None,
                PermissionModeOption.AcceptEdits,
                PermissionModeOption.BypassPermissions,
                PermissionModeOption.Plan,
            },
            values);
    }

    [Fact]
    public void PermissionModeChoices_NoneChoiceHasDefaultDisplayName()
    {
        var noneChoice = System.Array.Find(
            System.Linq.Enumerable.ToArray(CommonCliFlags.PermissionModeChoices),
            choice => choice.Value == PermissionModeOption.None);

        Assert.NotNull(noneChoice);
        Assert.Equal("Default", noneChoice!.DisplayName);
    }
}
