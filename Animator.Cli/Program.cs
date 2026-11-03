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

			  harlequin import <project> <file>... [--no-rig] [--no-split] [--scale S] [--rig FILE]
			      Animate projects (an XFL folder, .xfl, DOMDocument.xml, .fla, zipped XFL) and Photoshop
			      files (.psd) in as objects. Makes the project if it isn't there. --rig: a bone for every
			      part at its symbol's origin, hung as the file's [part, parent] rules say (Animate only).

			  harlequin bring-in <project> --object NAME --bones B[,B...] <animate file>... [--category Head]
			      [--animations] [--loose] [--no-accessories] [--except WORD,...]
			      [--map THEIRS=OURS,...] [--include PART,...] [--by-bones] [--imported-from FILE]
			      [--rig FILE] [--copy-missing WORD,...] [--fit WORD,...] [--verbose]
			      The parts on those bones (and below), from each Animate project, as a skin of the object
			      named after the file (zombie_egypt_basic → egypt). --loose: as their own rest pose has them,
			      not as they show in their animations. --animations: their animations too. --except: parts
			      with these words in their names stay out (armor,ink…; =name: just that name). --map: their part goes on ours, for
			      parts whose names don't match (zombie_summernights_105x74=zombie_skull). --include: their parts
			      that come too wherever their bones are (or, without --bones, the only ones: a body skin).
			      --animations copies theirs by where their parts show, frame by frame (any rig; with no bones
			      or parts named, just the animations); --by-bones: bone for bone instead (for matching rigs).
			      --imported-from: the Animate project the object came from, to place everything exactly
			      through the symbols. --copy-missing hand: their parts and drawings with those words that
			      the object has nothing like, copied for the animations. --fit arm,leg: those parts (limbs)
			      fitted by their joints, where theirs are drawn longer or shorter (with --imported-from).
			      --verbose: which part went where.

			  harlequin skin <project> --object NAME --name SKIN --parts P,... [--category C] [--overlay] [--on]
			      [--from SKIN]
			      A skin of those parts' drawings (the parts then show only while it's on). Made off, unless --on.
			      --from: their drawings taken out of that skin (a hat out of a head skin, into a skin of its own).

			  harlequin order <project> --object NAME --parts P,... (--behind Q | --in-front-of Q) [--animation A]
			      Parts moved in the draw order: at rest (and in the animations' keys that drew them as
			      the rest order did), or with --animation, all through that animation.

			  harlequin show <project> --object NAME [--skins A,B] [--hide A,B] [--none]
			      Which skins are on (as the project opens, and in exports).

			  harlequin rename <project> --object NAME [--skin SKIN | --animation A] --to NEW
			      The object, or one of its skins or animations.
			  harlequin remove <project> --object NAME [--animations A,B] [--skins A,B] [--parts P,Q]
			      Without any of those, the whole object goes.

			  harlequin render <project> --out FILE.png [--object NAME] [--animation A] [--frame N | --time T]
			      [--skins A,B] [--scale S] [--background RRGGBB] [--sheet CATEGORY|all] [--columns N]
			      A picture of an object (at rest, or a frame of an animation). --sheet: one picture of every
			      skin in the category side by side, named.

			  harlequin pose <project> [--object NAME] [--animation A] [--frame N | --time T] [--skins A,B] [--sweep K]
			      Every part shown then, front last: its drawing, its picture's middle, turn and size.
			      --sweep K: the whole animation, K times a frame (as it plays between keys), each line timed.

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
					"bring-in" => Commands.BringIn(new Args(rest, "animations", "loose", "no-accessories", "by-bones", "verbose")),
					"show" => Commands.Show(new Args(rest, "none")),
					"skin" => Commands.Skin(new Args(rest, "overlay", "on")),
					"rename" => Commands.Rename(new Args(rest)),
					"order" => Commands.Order(new Args(rest)),
					"remove" => Commands.Remove(new Args(rest)),
					"render" => Commands.Render(new Args(rest)),
					"pose" => Commands.Pose(new Args(rest)),
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
