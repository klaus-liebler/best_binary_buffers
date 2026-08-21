namespace BestBinaryBuffers.Tests;

// One schema exercising every field kind BestBinaryBuffers supports, shared by several test files so
// there's a single place to update if the DSL surface changes.
internal static class Fixtures
{
	public const string FullSchema = """
		using BestBinaryBuffers;

		namespace testns;

		[BinaryType]
		public enum Color : byte
		{
			Red,
			Green = 5,
			Blue,
		}

		[BinaryType]
		public struct Vector3
		{
			public float X;
			public float Y;
			public float Z;
		}

		[BinaryType]
		public struct Mac
		{
			[BinaryCount(6)] public byte[] Bytes;
		}

		[BinaryType]
		public class DeviceInfo
		{
			public ushort Id;
			[BinaryMaxEncodedByteLength(31)] public string Name;
		}

		[BinaryUnion]
		public interface ICommand { }

		[BinaryType]
		public class LightCommand : ICommand
		{
			public byte Brightness;
		}

		[BinaryType]
		public class TextCommand : ICommand
		{
			[BinaryMaxEncodedByteLength(63)] public string Text;
		}

		[BinaryType]
		public class Wrapper
		{
			[BinaryMaxEncodedByteLength(31)] public string Label;
			public ICommand Selected;
		}

		[BinaryMessage(MessageKind.Request)]
		public class RequestPing
		{
		}

		[BinaryMessage(MessageKind.Response)]
		public class ResponseStatus
		{
			public uint Timestamp;
			public Vector3 Position;
			public Color Mood;
			public Mac Address;
			[BinaryMaxItemCount(8)] public Vector3[] Waypoints;
			[BinaryMaxItemCount(4)] [BinaryMaxEncodedByteLength(15)] public string[] Tags;
			[BinaryMaxItemCount(4)] public ICommand[] Commands;
		}

		[BinaryMessage(MessageKind.Event)]
		public class NotifyThing
		{
			[BinaryMaxEncodedByteLength(127)] public string Message;
		}
		""";
}
