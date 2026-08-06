using Avalonia.Headless;
using Ksp2Redux.Tools.Launcher.Tests.HeadlessTests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]
// All AvaloniaTests should run separately, the application will be rebuilt everytime
// Others tests are running in parallel
[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerTest)]
