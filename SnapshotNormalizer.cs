using System.Collections.Generic;

// One normalizer and one validator for the on-disk snapshot records.
//
// entries/*.json is user-editable - the README documents the schema and the format is deliberately
// stable so a future import can read it back - and the JSON layer is forgiving: camelCase keys map
// onto the records and anything missing is left at its default, so a truncated or hand-edited file
// deserializes "successfully" with a null Entries list, or entries with a null Task/Description.
// Every reader used to guard those fields on its own (Resume guarded some of them, LoadReadOnly
// others), which is exactly how two load paths drift apart and one of them starts crashing.
//
// So: Normalize() is the single repair step every load path runs, and TryValidate() is the single
// answer to "may this file be offered to the user at all?" for the list-then-choose screens.
internal static class SnapshotNormalizer
{
    internal const string UnnamedSessionName = "Unnamed session";

    // The task name the rest of the app uses for an entry that has none ("blank means none" is the
    // rule StartNewEntryWithTask and ApplyEntryUpdate already apply to live input).
    internal const string NoTaskName = "none";

    // The oldest on-disk format this build reads. v1 predates isDeleted (the record default is false),
    // which is exactly the legacy behavior the validator has to keep accepting. A schemaVersion below
    // this - including 0, which is what a file with the key missing OR explicitly set to 0 deserializes
    // to - describes a format this build cannot promise to render, so it is refused rather than offered.
    internal const int OldestSupportedSchemaVersion = 1;

    // Semantic validation, not JSON validation (a file that is not JSON at all is already rejected
    // by the deserializer). Only content the app cannot represent is refused here, so that
    // ListAllSessions cannot offer a session that the renderers would then choke on.
    //
    // A null snapshot = the deserializer refused the file. A schemaVersion outside
    // [OldestSupportedSchemaVersion, EntryStore.SchemaVersion] = a file whose semantics this build
    // cannot promise to render: > current is a future format, < oldest is an absent (missing key ->
    // 0) or unsupported-old format. Offering one of those would also mean persisting it as a usable
    // session on the next flush. Everything else that deserializes is repaired by Normalize:
    // repairing is reversible (the file is untouched until the user resumes it, and the user's own
    // file is never rewritten by a listing).
    internal static bool TryValidate(SessionSnapshot? snapshot, out string reason)
    {
        if(snapshot is null)
        {
            reason = "not readable as a session snapshot";
            return false;
        }

        if(snapshot.SchemaVersion > EntryStore.SchemaVersion)
        {
            reason = $"schema version {snapshot.SchemaVersion} is newer than this build supports ({EntryStore.SchemaVersion})";
            return false;
        }

        if(snapshot.SchemaVersion < OldestSupportedSchemaVersion)
        {
            reason = $"schema version {snapshot.SchemaVersion} is not supported (a missing schemaVersion reads as 0); this build reads schema versions {OldestSupportedSchemaVersion}..{EntryStore.SchemaVersion}";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    // Repairs every field the display and action paths assume is non-null:
    //   Name                  null -> "Unnamed session"
    //   Entries               null (missing or truncated) -> empty list
    //   Entries[i]            null array element -> dropped
    //   Entries[i].Task       null -> "none"
    //   Entries[i].Description null -> ""
    // Duplicate ids are collapsed to one entry (last occurrence wins) so the id -> entry dictionary
    // the session builds cannot silently lose a different entry per reader.
    //
    // Nothing here invents data for a field that is merely absent: a missing startTime stays the
    // default DateTime, exactly as before. (A missing schemaVersion also stays 0, but such a file is
    // refused by TryValidate before Normalize ever sees it - Normalize never turns an unsupported
    // format into an offerable one.) An entry that exists but carries no task is the one case that
    // gets a value, because "none" is what the app itself stores for an entry with no task.
    internal static SessionSnapshot Normalize(SessionSnapshot snapshot) => snapshot with
    {
        Name = string.IsNullOrWhiteSpace(snapshot.Name) ? UnnamedSessionName : snapshot.Name,
        Entries = NormalizeEntries(snapshot.Entries)
    };

    private static List<EntrySnapshot> NormalizeEntries(List<EntrySnapshot>? entries)
    {
        List<EntrySnapshot> normalized = new();

        if(entries is null) return normalized;

        // File order is preserved (oldest id first, as TakeSnapshot writes it) while duplicates are
        // collapsed, so the resulting entry set is deterministic for a given file.
        Dictionary<int, EntrySnapshot> byId = new();
        List<int> order = new();

        foreach(EntrySnapshot? entry in entries)
        {
            if(entry is null) continue;

            if(!byId.ContainsKey(entry.Id)) order.Add(entry.Id);

            byId[entry.Id] = entry with
            {
                Task = entry.Task ?? NoTaskName,
                Description = entry.Description ?? string.Empty
            };
        }

        foreach(int id in order) normalized.Add(byId[id]);

        return normalized;
    }
}
