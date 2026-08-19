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
	// checks there). This pass only checks the one cross-cutting rule that needs the whole, final field
	// list at once: at most one polymorphic field (PolymorphicField/PolymorphicArrayField) per owner,
	// and it must be the very last field.
	//
	// Why: decoding a polymorphic field is DEFERRED (the owner's Decode() just records the classId/
	// element count and jumps straight to the end of the frame; actual per-element decoding happens
	// later, on demand, via a separate visitor function, to avoid double-parsing). That only works if
	// nothing else in the frame follows it, and if there's only one such field per owner. This is a
	// deliberate tightening vs. the JSON-schema generator this library replaces, which allowed several
	// consecutive trailing class-array fields but had a latent decode bug for exactly that case (the
	// second trailing field's generated Decode() always read starting from a cursor already forced to
	// the frame's end).
	public static void ValidateFields(IReadOnlyList<FieldBase> fields, string ns, string ownerDescription)
	{
		// Requiring every polymorphic field to be the LAST field already implies "at most one": with two
		// or more, at least one of them can never be last and trips this check -- no separate "at most
		// one" pass needed.
		for (var i = 0; i < fields.Count; i++)
		{
			if (fields[i] is not (PolymorphicArrayField or PolymorphicField)) continue;
			if (i != fields.Count - 1)
			{
				throw new SchemaException(
					$"{ownerDescription}, Feld \"{fields[i].Name}\": ein polymorphes Feld ([BinaryUnion]-Typ oder -Array) muss das " +
					"letzte Feld sein und es darf nur eines pro Typ geben -- danach duerfen keine weiteren Felder folgen " +
					"(Decodierung ist deferred bis zum Frame-Ende).");
			}
		}

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
