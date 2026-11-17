using BestBinaryBuffers.Parsing;

namespace BestBinaryBuffers.Tests;

public class ValidationTests
{
	private static void ExpectSchemaError(string content, string expectedSubstring)
	{
		using var schema = new TestSchema();
		schema.AddFile(content);
		var ex = Assert.Throws<SchemaException>(() => SchemaParser.Parse(schema.Files, new IdMap()));
		Assert.Contains(expectedSubstring, ex.Message);
	}

	[Fact]
	public void StringFieldInStructIsRejected() => ExpectSchemaError("""
		using BestBinaryBuffers;
		namespace ns;
		[BinaryType]
		public struct Bad { public string Name; }
		""", "Struct");

	[Fact]
	public void ArrayWithoutBinaryCountInStructIsRejected() => ExpectSchemaError("""
		using BestBinaryBuffers;
		namespace ns;
		[BinaryType]
		public struct Bad { public int[] Values; }
		""", "BinaryCount");

	[Fact]
	public void UnboundedArrayInClassIsRejected() => ExpectSchemaError("""
		using BestBinaryBuffers;
		namespace ns;
		[BinaryType]
		public class Bad { public int[] Values; }
		""", "[BinaryMaxItemCount");

	// Variable arrays are allowed in classes (not only messages) since 2026-11-17.
	[Fact]
	public void BoundedArraysInClassAreAccepted() => ExpectValid("""
		using BestBinaryBuffers;
		namespace ns;
		[BinaryUnion] public interface IU { }
		[BinaryType] public class A : IU { public byte V; }
		[BinaryUnion] public interface IOuter { }
		[BinaryType]
		public class Ok : IOuter
		{
			[BinaryMaxItemCount(8)] public ushort[] Outputs;
			[BinaryMaxItemCount(4)] [BinaryMaxEncodedByteLength(7)] public string[] Tags;
			[BinaryMaxItemCount(4)] public IU[] Parts;
			public byte After;
		}
		""");

	[Fact]
	public void RecursiveClassViaUnionIsRejected() => ExpectSchemaError("""
		using BestBinaryBuffers;
		namespace ns;
		[BinaryUnion] public interface IU { }
		[BinaryType] public class Leaf : IU { public byte V; }
		[BinaryType] public class Node : IU { [BinaryMaxItemCount(4)] public IU[] Children; }
		[BinaryMessage(MessageKind.Event)]
		public class Tree { public IU Root; }
		""", "rekursive Klassen-Referenz");

	private static void ExpectValid(string content)
	{
		using var schema = new TestSchema();
		schema.AddFile(content);
		SchemaParser.Parse(schema.Files, new IdMap());
	}

	// Polymorphic fields carry a byte-length prefix, so -- unlike before -- they may appear anywhere
	// and more than once per owner (s. SchemaValidation).
	[Fact]
	public void PolymorphicFieldFollowedByOtherFieldsIsAccepted() => ExpectValid("""
		using BestBinaryBuffers;
		namespace ns;
		[BinaryUnion] public interface IU { }
		[BinaryType] public class A : IU { public byte V; }
		[BinaryMessage(MessageKind.Event)]
		public class Ok
		{
			public IU Choice;
			public int After;
		}
		""");

	[Fact]
	public void SeveralPolymorphicFieldsAreAccepted() => ExpectValid("""
		using BestBinaryBuffers;
		namespace ns;
		[BinaryUnion] public interface IU { }
		[BinaryType] public class A : IU { public byte V; }
		[BinaryType] public class Holder { public IU Inner; public ushort After; }
		[BinaryMessage(MessageKind.Event)]
		public class Ok
		{
			public IU First;
			[BinaryMaxItemCount(4)] public IU[] Many;
			public IU Second;
		}
		""");

	[Fact]
	public void UnknownTypeReferenceIsRejected() => ExpectSchemaError("""
		using BestBinaryBuffers;
		namespace ns;
		[BinaryMessage(MessageKind.Event)]
		public class Bad { public DoesNotExist Value; }
		""", "unbekannter Typ");

	[Fact]
	public void CyclicStructReferenceIsRejected() => ExpectSchemaError("""
		using BestBinaryBuffers;
		namespace ns;
		[BinaryType] public struct A { public B Inner; }
		[BinaryType] public struct B { public A Inner; }
		""", "zyklische Struct-Referenz");

	[Fact]
	public void DuplicateDeclarationIsRejected() => ExpectSchemaError("""
		using BestBinaryBuffers;
		namespace ns;
		[BinaryType] public struct A { public int X; }
		[BinaryType] public struct A { public int Y; }
		""", "mehrfach deklariert");

	[Fact]
	public void ClassWithBothBinaryTypeAndBinaryMessageIsRejected() => ExpectSchemaError("""
		using BestBinaryBuffers;
		namespace ns;
		[BinaryType]
		[BinaryMessage(MessageKind.Event)]
		public class Bad { public int X; }
		""", "nur eines von beiden");

	[Fact]
	public void NestedNamespaceIsRejected() => ExpectSchemaError("""
		using BestBinaryBuffers;
		namespace outer
		{
			namespace inner
			{
				[BinaryType] public struct A { public int X; }
			}
		}
		""", "flach");

	[Fact]
	public void DottedNamespaceNameIsRejected() => ExpectSchemaError("""
		using BestBinaryBuffers;
		namespace outer.inner;
		[BinaryType] public struct A { public int X; }
		""", "flach");

	[Fact]
	public void NonPublicFieldIsRejected() => ExpectSchemaError("""
		using BestBinaryBuffers;
		namespace ns;
		[BinaryType]
		public struct Bad { private int X; }
		""", "public");

	[Fact]
	public void PrimitiveArrayElementIsRejectedForRepeatedField() => ExpectSchemaError("""
		using BestBinaryBuffers;
		namespace ns;
		[BinaryType]
		public struct Bad { [BinaryCount(4)] public string[] Names; }
		""", "nicht erlaubt");

	[Fact]
	public void ArrayFieldNamedLikeItsNamespaceIsRejected() => ExpectSchemaError("""
		using BestBinaryBuffers;
		namespace ns;
		[BinaryType]
		public struct Item { public byte V; }
		[BinaryMessage(MessageKind.Event)]
		public class Bad { [BinaryMaxItemCount(4)] public Item[] Ns; }
		""", "derzeit nicht unterstuetzt");

	[Fact]
	public void RepeatedFieldNamedLikeItsNamespaceIsRejected() => ExpectSchemaError("""
		using BestBinaryBuffers;
		namespace ns;
		[BinaryType]
		public struct Bad { [BinaryCount(2)] public byte[] Ns; }
		""", "derzeit nicht unterstuetzt");

	[Fact]
	public void RealCSharpSyntaxErrorSurfacesAsSchemaException() => ExpectSchemaError("""
		using BestBinaryBuffers;
		namespace ns
		[BinaryType] public struct A { public int X; }
		""", "Syntaxfehler");
}
