using Animator.Cli;

// harlequin <command> <project.hqnap> [options]: see Usage below, and docs/cli.md
return Cli.Run(args);

namespace Animator.Cli
{
	public static class Cli
	{
		public const string Usage = """
			harlequin: Harlequin on the command line. Projects are the editor's .hqnap files.

			  harlequin info <project> [--object NAME] [--tree]
			      What's in a project: its objects, their animations and skins (--tree: the bones and parts too).

			  harlequin import <project> <file>... [--no-rig] [--no-split] [--scale S]
			      Animate projects (an XFL folder, .xfl, DOMDocument.xml, .fla, zipped XFL) and Photoshop
			      files (.psd) in as objects. Makes the project if it isn't there.

			  harlequin bring-in <project> --object NAME --bones B[,B...] <animate file>... [--category Head]
			      [--animations] [--loose] [--no-accessories]
			      The parts on those bones (and below), from each Animate project, as a skin of the object
			      named after the file (zombie_egypt_basic → egypt). --loose: as their own rest pose has them,
			      not as they show in their animations. --animations: their animations too.

			  harlequin show <project> --object NAME [--skins A,B] [--hide A,B] [--none]
			      Which skins are on (as the project opens, and in exports).

			  harlequin rename <project> --object NAME --to NEW
			  harlequin remove <project> --object NAME [--animations A,B] [--skins A,B]
			      Without --animations or --skins, the whole object goes.

			  harlequin render <project> --out FILE.png [--object NAME] [--animation A] [--frame N | --time T]
			      [--skins A,B] [--scale S] [--background RRGGBB] [--sheet CATEGORY|all] [--columns N]
			      A picture of an object (at rest, or a frame of an animation). --sheet: one picture of every
			      skin in the category side by side, named.

			  harlequin export <project> --format engine|png|gif|sheet --out PATH [--object NAME]
			      [--animations A,B] [--scale S] [--bake] [--pixel-art] [--no-events] [--background RRGGBB]
			      As the editor's File → Export. With --object, that object's animations; else the scene.

			  harlequin version
			""";

		public static int Run(string[] args)
		{
			if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
			{
				Console.WriteLine(Usage);
				return args.Length == 0 ? 1 : 0;
			}
			string command = args[0].ToLowerInvariant();
			try
			{
				var rest = args.Skip(1);
				return command switch
				{
					"version" or "--version" => Version(),
					"info" => Commands.Info(new Args(rest, "tree")),
					"import" => Commands.Import(new Args(rest, "no-rig", "no-split")),
					"bring-in" => Commands.BringIn(new Args(rest, "animations", "loose", "no-accessories")),
					"show" => Commands.Show(new Args(rest, "none")),
					"rename" => Commands.Rename(new Args(rest)),
					"remove" => Commands.Remove(new Args(rest)),
					"render" => Commands.Render(new Args(rest)),
					"export" => Commands.Export(new Args(rest, "bake", "pixel-art", "no-events")),
					_ => throw new UsageException($"There's no command \"{args[0]}\".")
				};
			}
			catch (UsageException ex)
			{
				Console.Error.WriteLine($"harlequin {command}: {ex.Message}");
				Console.Error.WriteLine("(harlequin help: the commands and their options)");
				return 2;
			}
			catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or InvalidOperationException
				or UnauthorizedAccessException or System.Xml.XmlException or System.Text.Json.JsonException)
			{
				Console.Error.WriteLine($"harlequin {command}: {ex.Message}");
				return 1;
			}
		}

		private static int Version()
		{
			var v = typeof(Cli).Assembly.GetName().Version;
			Console.WriteLine($"harlequin {v} (project format {ProjectFile.FormatVersion})");
			return 0;
		}
	}
}
