using Microsoft.Extensions.Logging;

using NoGaReader.Mobile.Services;

using NoGaReader.Services;

namespace NoGaReader.Mobile;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var dataRoot = Path.Combine(FileSystem.AppDataDirectory, "NoGaReader");
		AppPaths.ConfigureRoot(dataRoot);
		SQLitePCL.Batteries_V2.Init();

		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

		builder.Services.AddSingleton<MobileLibraryService>();
		builder.Services.AddSingleton<MainPage>();

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
