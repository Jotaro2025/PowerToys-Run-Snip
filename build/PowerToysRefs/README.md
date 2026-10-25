# PowerToys reference assemblies for CI / local stub builds

Minimal copies of PowerToys host assemblies (`Wox.Plugin`, `PowerToys.ManagedCommon`,
`PowerToys.Settings.UI.Lib`, `PowerToys.Common.UI`) so the plugin can compile without a
full PowerToys install. `Wox.Plugin`'s transitive dependencies (`NLog`,
`TestableIO.System.IO.Abstractions.Wrappers`) are supplied to the test project as NuGet
PackageReferences — without them the test host crashes with FileNotFoundException when
plugin code calls `Wox.Plugin.Logger.Log`.

```bash
dotnet test -p:EnableWindowsTargeting=true -p:PowerToysInstallDir="$(pwd)/build/PowerToysRefs"
```

Prefer a real PowerToys install for day-to-day development when available.
