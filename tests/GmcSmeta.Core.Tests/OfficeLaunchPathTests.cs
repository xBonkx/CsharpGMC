using GmcSmeta.Core;

namespace GmcSmeta.Core.Tests;

public sealed class OfficeLaunchPathTests
{
    [Fact]
    public void OnlyInstalledOfficeVersionRedirectsToStableLauncher()
    {
        var local = Path.Combine(Path.GetTempPath(), "office-launch-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(local, "GMC-Smeta-Office");
        Directory.CreateDirectory(root);
        try
        {
            var launcher = Path.Combine(root, OfficeLaunchPath.LauncherFileName);
            File.WriteAllText(launcher, "");
            string Exe(string folder) => Path.Combine(root, "apps", folder, InstalledUpdateTarget.AppExeName);
            Assert.Equal(launcher, OfficeLaunchPath.Resolve(local, Exe("1.0.2-fx")));
            Assert.Equal(launcher, OfficeLaunchPath.Resolve(local, Exe("1.0.3-sc-123456")));
            Assert.Null(OfficeLaunchPath.Resolve(local, Exe("staging")));
            Assert.Null(OfficeLaunchPath.Resolve(local, Exe("1.0.2-fx-bad")));
            Assert.Null(OfficeLaunchPath.Resolve(local, Path.Combine(local, "GMC-Smeta-Next", "apps",
                "1.0.2-fx", InstalledUpdateTarget.AppExeName)));
            Assert.Null(OfficeLaunchPath.Resolve(local, Path.Combine(root, "apps", "1.0.2-fx", "other.exe")));
            Assert.Null(OfficeLaunchPath.Resolve(local, "relative.exe"));
            Assert.Null(OfficeLaunchPath.Resolve(null, Exe("1.0.2-fx")));
            File.Delete(launcher);
            Assert.Null(OfficeLaunchPath.Resolve(local, Exe("1.0.2-fx")));
        }
        finally { Directory.Delete(local, true); }
    }
}
