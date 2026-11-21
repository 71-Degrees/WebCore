# Audit trail

Who changed what, when, and from which request, for any 71 Degrees app. Lifted from Better Trivia (2026-11-03) and CrateDigger (2026-11-16) on 2026-11-16, so a new app
gets it in two calls instead of its own copy.

## What it records

One `AuditEntry` per save, delete or switched flag: the time, the actor (the signed-in person with the request's trace id and path, the key-gated API, or `system`),
the item's kind, id and name, whose it is (optional), the action (`created`, `changed`, `deleted`) and, for a change, each field that changed with its value before and
after (`AuditDiff`). A save that changed nothing writes nothing. Writing the trail never fails the change: a fault is logged as an error with the whole entry.

Kept in the app's own `audit` store: `item-{kind}-{id}.json` per item, `owner-{id}.json` per owner (only when the entry names one), `actor-{id}.json` per actor (the
item histories that actor wrote into, so forgetting them needs no listing) and `recent.json`, each capped (`AuditLogOptions`: 200 per item, 500 per owner, 1000
recent) and entries older than 180 days dropped as they are written. `ForgetOwnerAsync` ("delete my data") removes every entry the person owns: their own list, their
items' histories and the recent list. Their actions on other owners' items stay, with the actor anonymised (id `forgotten`, name kept, trace id and path cleared), so the
other owner's history keeps its shape. Entries written before the actor index existed are found through the person's own items and the recent list only; the rest age
out with `KeepFor`.

## Using it in an app

```csharp
services.AddAuditTrail(storage.Documents(AppStores.Audit), new AuditTrailOptions
{
    IgnoredFields = new HashSet<string>(StringComparer.Ordinal) { "UpdatedAt", "Fingerprints" },   // top-level fields no person needs to read
});
...
app.UseAuthentication();
app.UseAuditActors();          // after authentication (and any dev auto sign-in)
app.UseAuthorization();
```

Then wrap each repository whose writes matter, as the outermost layer, so no service can skip it:

```csharp
public sealed class AuditedShopRepository(IShopRepository inner, ChangeAuditor auditor) : IShopRepository
{
    public async Task<Shop> SaveAsync(Shop shop, CancellationToken ct = default)
    {
        var before = await inner.GetAsync(shop.Id, ct);
        var saved = await inner.SaveAsync(shop, ct);
        await auditor.SavedAsync(new AuditSubject("shop", saved.Id, saved.Name, saved.Id), before, saved, ct);
        return saved;
    }
    ...
}
```

Read it with `IAuditLog` (`ForItemAsync` for an item's history card, `ForOwnerAsync` for a person, `RecentAsync` for an admin page). Edits made inside a Blazor
circuit get the circuit's person as the actor (`AuditCircuitHandler`, registered by `AddAuditTrail`). Add a line to the app's privacy page and its "Your data"
deletion (`ForgetOwnerAsync`), and a row to its performance inventory: each audited write costs one read of the item as it was and up to three trail writes.
