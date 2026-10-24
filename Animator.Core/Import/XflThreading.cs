using System.Numerics;

namespace Animator.Core.Import;

// Which layers' frames make up each part, for projects whose layers don't keep to one part.
// Animate projects do; converters (the ones writing PvZ2 animations as XFL, say) put a part on one
// layer in one animation and on another in the next, and lend a layer to something else for a while
// (a crumb flying off on the arm's layer while the arm is on another). So each layer's frames are
// cut into pieces where it shows nothing and where an animation starts, and each piece, in the
// order they start, carries on the part that last showed one of its drawings and isn't on stage
// then: one on the same layer if it can (carrying straight on, best), else the one whose main
// drawing it shows, nearest to where it starts. A piece sharing no drawing with any part free then
// starts a part of its own.
public static class XflThreading
{
	// Frames Start..End (both shown) of one layer's track
	public readonly record struct Piece(int Track, int Start, int End);

	private sealed class Part
	{
		public readonly List<Piece> Pieces = new();
		public readonly Dictionary<XflDrawingKey, int> Shown = new();   // frames each drawing shows
		public int End = -1;
		public Vector2 LastAt;
		public XflDrawingKey Main => Shown.MaxBy(s => s.Value).Key;
	}

	// tracks: each layer track's placements; cuts: frames where an animation starts
	public static List<List<Piece>> ByDrawing(IReadOnlyList<XflPlacement?[]> tracks, ISet<int> cuts, int length)
	{
		var pieces = new List<(Piece Piece, Dictionary<XflDrawingKey, int> Shown, XflDrawingKey Main, Vector2 First, Vector2 Last)>();
		for (int t = 0; t < tracks.Count; t++)
		{
			var frames = tracks[t];
			int start = -1;
			for (int f = 0; f <= length; f++)
			{
				bool shown = f < length && f < frames.Length && frames[f] != null;
				if (start >= 0 && (!shown || cuts.Contains(f)))
				{
					var drawings = new Dictionary<XflDrawingKey, int>();
					for (int g = start; g < f; g++)
						drawings[frames[g]!.Value.Drawing] = drawings.GetValueOrDefault(frames[g]!.Value.Drawing) + 1;
					pieces.Add((new Piece(t, start, f - 1), drawings, drawings.MaxBy(d => d.Value).Key,
						frames[start]!.Value.Matrix.Translation, frames[f - 1]!.Value.Matrix.Translation));
					start = -1;
				}
				if (shown && start < 0) start = f;
			}
		}
		pieces.Sort((a, b) => a.Piece.Start != b.Piece.Start ? a.Piece.Start.CompareTo(b.Piece.Start) : a.Piece.Track.CompareTo(b.Piece.Track));

		var parts = new List<Part>();
		foreach (var (piece, drawings, main, first, last) in pieces)
		{
			Part? best = null;
			(int, int, int, float, int) bestScore = default;
			foreach (var part in parts)
			{
				if (part.End >= piece.Start || !drawings.Keys.Any(part.Shown.ContainsKey)) continue;
				var before = part.Pieces[^1];
				var score = (before.Track == piece.Track && before.End == piece.Start - 1 ? 1 : 0,
					before.Track == piece.Track ? 1 : 0,
					part.Main.Equals(main) ? 1 : 0,
					-Vector2.Distance(part.LastAt, first),
					part.End);
				if (best != null && score.CompareTo(bestScore) <= 0) continue;
				best = part;
				bestScore = score;
			}
			if (best == null) parts.Add(best = new Part());
			best.Pieces.Add(piece);
			foreach (var (drawing, frames) in drawings) best.Shown[drawing] = best.Shown.GetValueOrDefault(drawing) + frames;
			best.End = piece.End;
			best.LastAt = last;
		}
		return parts.Select(p => p.Pieces).ToList();
	}

	// Each layer track as one part, its pieces where it's shown
	public static List<List<Piece>> ByLayer(IReadOnlyList<XflPlacement?[]> tracks, int length)
	{
		var parts = new List<List<Piece>>();
		for (int t = 0; t < tracks.Count; t++)
		{
			var pieces = new List<Piece>();
			int start = -1;
			for (int f = 0; f <= length; f++)
			{
				bool shown = f < length && f < tracks[t].Length && tracks[t][f] != null;
				if (start >= 0 && !shown)
				{
					pieces.Add(new Piece(t, start, f - 1));
					start = -1;
				}
				if (shown && start < 0) start = f;
			}
			parts.Add(pieces);
		}
		return parts;
	}
}
