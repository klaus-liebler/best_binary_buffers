using BestBinaryBuffers.Model;

namespace BestBinaryBuffers.Parsing;

internal enum OwnerKind
{
	Struct,
	Class,
	Message,
}

internal static class SchemaValidation
{
	// Legality of WHICH field kinds an owner (Struct/Class/Message) may contain at all is already
	// enforced at field-resolution time (ParseContext.ResolveField only ever CREATES a
	// StringField/PolymorphicField/*ArrayField when the owner kind allows it -- see the ownerKind
	// checks there). This pass only checks rules that need the whole, final field list at once.
	//
	// Polymorphic fields (PolymorphicField/PolymorphicArrayField) may appear anywhere and any number of
	// times: they carry a byte-length prefix (u32) on the wire, so the deferred decoding (the owner's
	// Decode() only records start/count/byte length, the elements are decoded later by a visitor)
	// still knows where the next field starts. Formerly (without that prefix) a polymorphic field had to
	// be the single, last field of its owner, because Decode() could only jump to the end of the frame.
	public static void ValidateFields(IReadOnlyList<FieldBase> fields, string ns, string ownerDescription)
	{
		// Der TS-Generator legt fuer Sammelfelder (Array-artige Felder) in der generierten decode()-
		// Funktion eine lokale Variable an, die exakt so heisst wie das Feld selbst (z.B.
		// "const tasks: tasks.TaskInfo[] = []; for (...) tasks.push(tasks.decodeTaskInfo(r));"). Heisst
		// das Feld genauso wie der umschliessende Namespace, verdeckt diese lokale Variable ab ihrer
		// Deklaration den Namespace-Bezeichner -- ein nachfolgender Aufruf wie "tasks.decodeTaskInfo(...)"
		// loest sich dann faelschlich gegen die (leere) Array-Property statt gegen die Namespace-Funktion
		// auf und erzeugt fehlerhaften/nicht kompilierenden TypeScript-Code, ohne dass der Generator selbst
		// das bemerkt. Statt das (nicht trivial, s. Kommentar unten) im Generator selbst zu umgehen, wird
		// diese Kombination hier beim Parsen abgefangen.
		foreach (var field in fields)
		{
			var collidesWithNamespace = field is RepeatedField or UniformPackedArrayField or UniformVariableArrayField
				or PolymorphicArrayField or PolymorphicField;
			if (collidesWithNamespace && field.Name == ns)
			{
				throw new SchemaException(
					$"{ownerDescription}, Feld \"{field.Name}\": derzeit nicht unterstuetzt -- ein Sammelfeld (Array/Liste) darf nicht " +
					$"genauso heissen wie sein umschliessender Namespace (\"{ns}\"). Der generierte TypeScript-Code wuerde dadurch die " +
					"gleichnamige lokale Decode-Variable mit dem Namespace verwechseln (siehe Klassenkommentar in SchemaValidation.cs). " +
					"Feld umbenennen, z.B. auf \"items\".");
			}
		}
	}
}
