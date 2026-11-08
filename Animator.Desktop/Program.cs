using System.Runtime.InteropServices;
using Avalonia;
using Silk.NET.OpenAL;

namespace Animator.Desktop;

// harlequin-editor [project.hqnap]: the editor, with that project open (else a new character).
// harlequin-editor --check [report.txt]: no window, just whether the libraries it needs load
// (drawing, text, sound), said in the report (or on the console) and by the exit code.
public static class Program
{
	[STAThread]
	public static int Main(string[] args)
	{
		int check = Array.IndexOf(args, "--check");
		if (check >= 0) return Check(check + 1 < args.Length ? args[check + 1] : null);
		App.StartupFile = args.FirstOrDefault(a => !a.StartsWith('-'));
		return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
	}

	public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
		.UsePlatformDetect()
		.WithInterFont()
		.LogToTrace();

	// What CI runs on the published program: a library that won't load shows here, not on someone's screen
	private static int Check(string? reportPath)
	{
		var lines = new List<string> { $"Harlequin editor {typeof(Program).Assembly.GetName().Version?.ToString(3)} on {RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture})" };
		bool ok = true;
		void Try(string what, Func<string> test, bool required = true)
		{
			try
			{
				lines.Add($"ok    {what}: {test()}");
			}
			catch (Exception ex)
			{
				lines.Add($"{(required ? "FAIL" : "note")}  {what}: {ex.GetType().Name}: {ex.Message}");
				if (required) ok = false;
			}
		}
		Try("drawing (SkiaSharp)", () =>
		{
			using var bitmap = new SKBitmap(32, 32);
			using var canvas = new SKCanvas(bitmap);
			canvas.Clear(SKColors.White);
			using var paint = new SKPaint { Color = SKColors.Black };
			using var font = new SKFont(SKTypeface.Default, 14);
			canvas.DrawText("Hq", 2, 20, SKTextAlign.Left, font, paint);
			return $"a picture drawn, {SKTypeface.Default.FamilyName} for text";
		});
		Try("text shaping (HarfBuzz)", () =>
			NativeLibrary.TryLoad("libHarfBuzzSharp", typeof(Program).Assembly, null, out var harfBuzz) && harfBuzz != IntPtr.Zero
				? "its library loads" : throw new DllNotFoundException("libHarfBuzzSharp didn't load"));
		Try("sound (OpenAL Soft)", () =>
		{
			var alc = ALContext.GetApi(true);
			AL.GetApi(true);
			unsafe
			{
				var device = alc.OpenDevice(null!);
				if (device == null) return "its library loads (no sound device here)";
				alc.CloseDevice(device);
				return "its library loads, and the sound device opens";
			}
		});
		lines.Add(ok ? "All good." : "Something's missing: see FAIL above.");
		string report = string.Join(Environment.NewLine, lines);
		if (reportPath != null) File.WriteAllText(reportPath, report + Environment.NewLine);
		else Console.WriteLine(report);
		return ok ? 0 : 1;
	}
}
