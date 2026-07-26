namespace NoGaReader.Mobile;

public partial class App : Application
{
	private readonly IServiceProvider _services;

	public App(IServiceProvider services)
	{
		InitializeComponent();
		_services = services;
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		var mainPage = _services.GetRequiredService<MainPage>();
		return new Window(new NavigationPage(mainPage));
	}
}
