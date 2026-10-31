using Animator.Core.Export;

namespace Animator.Tests;

// Step 26: packing pictures into atlas pages
public class AtlasTests
{
	private static void NoOverlaps(AtlasPacker.Result result, int gap, int max)
	{
		foreach (var a in result.Placed)
		{
			var (pw, ph) = result.Pages[a.Page];
			Assert.True(pw <= max && ph <= max);
			Assert.True(a.X >= gap && a.Y >= gap && a.X + a.Width + gap <= pw && a.Y + a.Height + gap <= ph, $"{a} on a {pw} × {ph} page");
			foreach (var b in result.Placed)
			{
				if (a.Index >= b.Index || a.Page != b.Page) continue;
				bool apart = a.X + a.Width + gap <= b.X || b.X + b.Width + gap <= a.X || a.Y + a.Height + gap <= b.Y || b.Y + b.Height + gap <= a.Y;
				Assert.True(apart, $"{a} and {b} overlap (or touch inside the gap)");
			}
		}
	}

	[Fact]
	public void Pictures_are_packed_apart_on_as_few_pages_as_they_need()
	{
		var random = new Random(7);
		var sizes = Enumerable.Range(0, 60).Select(_ => (random.Next(4, 90), random.Next(4, 90))).ToList();
		var result = AtlasPacker.Pack(sizes, maxSize: 512, gap: 2);
		Assert.Equal(sizes.Count, result.Placed.Count);
		Assert.Equal(Enumerable.Range(0, 60), result.Placed.Select(p => p.Index));   // (in the order given)
		Assert.All(result.Placed, p => Assert.Equal(sizes[p.Index], (p.Width, p.Height)));
		Assert.Single(result.Pages);
		NoOverlaps(result, 2, 512);
		long area = sizes.Sum(s => (long)s.Item1 * s.Item2);
		Assert.True(area > result.Pages[0].Width * result.Pages[0].Height * 0.6, "packed tightly enough");

		var many = AtlasPacker.Pack(Enumerable.Repeat((200, 200), 9).ToList(), maxSize: 512, gap: 1);   // 4 to a page
		Assert.Equal(3, many.Pages.Count);
		NoOverlaps(many, 1, 512);

		var square = AtlasPacker.Pack(new[] { (100, 30) }, maxSize: 512, gap: 0, powerOfTwo: true);
		Assert.Equal((128, 64), square.Pages[0]);
		Assert.Throws<InvalidOperationException>(() => AtlasPacker.Pack(new[] { (600, 10) }, maxSize: 512));
	}
}
