using System.Text.RegularExpressions;

namespace Aiursoft.DMS.Contracts;

public sealed partial record PullerForceUpdateInput(string? Image = null, string Puller = "main")
{
    public string ImageScope { get; init; } = Image == null ? "AllConfiguredImages" : "SingleImage";

    public void Validate()
    {
        if (Image != null && !ImageName().IsMatch(Image))
            throw new ArgumentException("Supply one concrete image name with an optional tag, such as aiursoft/dms:latest.");
        if (Puller is not ("main" or "cn"))
            throw new ArgumentException("Select a Puller: main or cn.");
        if (ImageScope != (Image == null ? "AllConfiguredImages" : "SingleImage"))
            throw new ArgumentException("Image scope must match the selected image or all configured images.");
    }

    [GeneratedRegex(@"\A(?=.{1,255}\z)[a-z0-9]+(?:[._-][a-z0-9]+)*(?:/[a-z0-9]+(?:[._-][a-z0-9]+)*)*(?::[A-Za-z0-9_][A-Za-z0-9_.-]{0,127})?\z")]
    private static partial Regex ImageName();
}

public sealed record PullerForceUpdateResult(string? Image, string ContainerId, string Puller, string ImageScope);
