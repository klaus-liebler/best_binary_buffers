namespace BestBinaryBuffers.Tests;

public class IdMapTests
{
	[Fact]
	public void RepeatedCallsForSameNameReturnSameId()
	{
		var map = new IdMap();
		var first = map.GetOrAssignClass("ns.Foo");
		var second = map.GetOrAssignClass("ns.Foo");
		Assert.Equal(first, second);
	}

	[Fact]
	public void DifferentNamesGetIncrementingIds()
	{
		var map = new IdMap();
		var a = map.GetOrAssignClass("ns.A");
		var b = map.GetOrAssignClass("ns.B");
		Assert.Equal(a + 1, b);
	}

	[Fact]
	public void ClassIdsAreGlobalAcrossNamespaces()
	{
		var map = new IdMap();
		var a = map.GetOrAssignClass("ns1.A");
		var b = map.GetOrAssignClass("ns2.B");
		Assert.Equal(a + 1, b);
	}

	[Fact]
	public void MessageIdsAreScopedPerNamespace()
	{
		var map = new IdMap();
		var a1 = map.GetOrAssignMessage("ns1.A");
		var b1 = map.GetOrAssignMessage("ns2.B"); // different namespace -> starts its own counter at 1
		Assert.Equal(1, a1);
		Assert.Equal(1, b1);
	}

	[Fact]
	public void NullNamespaceAlwaysGetsIdZeroAndIsNeverPersisted()
	{
		var map = new IdMap();
		Assert.Equal(0, map.GetOrAssignNamespace(""));
		Assert.Equal(0, map.GetOrAssignNamespace(""));
	}

	[Fact]
	public void SaveThenLoadRoundTripsAssignedIds()
	{
		var path = Path.Combine(Path.GetTempPath(), $"bbb_idmap_{Guid.NewGuid():N}.txt");
		try
		{
			var map = new IdMap();
			var classId = map.GetOrAssignClass("ns.Foo");
			var nsId = map.GetOrAssignNamespace("ns");
			var msgId = map.GetOrAssignMessage("ns.Bar");
			map.SaveIfDirty(path);

			var reloaded = IdMap.Load(path);
			Assert.Equal(classId, reloaded.GetOrAssignClass("ns.Foo"));
			Assert.Equal(nsId, reloaded.GetOrAssignNamespace("ns"));
			Assert.Equal(msgId, reloaded.GetOrAssignMessage("ns.Bar"));
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public void ExistingEntriesSurviveANewNameBeingAdded()
	{
		var path = Path.Combine(Path.GetTempPath(), $"bbb_idmap_{Guid.NewGuid():N}.txt");
		try
		{
			var map = new IdMap();
			var first = map.GetOrAssignClass("ns.Foo");
			map.SaveIfDirty(path);

			var reloaded = IdMap.Load(path);
			var stillFirst = reloaded.GetOrAssignClass("ns.Foo");
			var second = reloaded.GetOrAssignClass("ns.Bar"); // new name appended, must not renumber Foo
			reloaded.SaveIfDirty(path);

			Assert.Equal(first, stillFirst);

			var reloadedAgain = IdMap.Load(path);
			Assert.Equal(first, reloadedAgain.GetOrAssignClass("ns.Foo"));
			Assert.Equal(second, reloadedAgain.GetOrAssignClass("ns.Bar"));
		}
		finally
		{
			File.Delete(path);
		}
	}
}
