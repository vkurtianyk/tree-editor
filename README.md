# Tree editor

Two trees side by side. **Database** (left) reads a large tree lazily from PostgreSQL. **Cache** (right) is a local
cache in the browser: load single elements into it, then add, rename and delete them offline, and **Apply** the
changes in one transaction. Conflicts with the database are shown per element.

.NET 10 · Aspire 13 · PostgreSQL 18 · EF Core 10 · Blazor WebAssembly (served by the API) · MudBlazor.
Every decision, with its reasons and the options rejected: [docs/DECISIONS.md](docs/DECISIONS.md).

## Run

Prerequisites:

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (10.0.100 or later, see `global.json`)
- Docker, running (the database is a container)
- Aspire CLI: `curl -sSL https://aspire.dev/install.sh | bash` (Windows: `irm https://aspire.dev/install.ps1 | iex`)

From the repo root:

```bash
aspire run
```

Without the Aspire CLI: `dotnet run --project src/TreeEditor.AppHost`.

This starts PostgreSQL, runs the MigrationService (migrate + seed, then exit) and then starts the API, which also
serves the UI. The API is up about 20 s after the command once the Postgres image is pulled. Stop everything with
Ctrl+C.

| What             | Where                                                                             |
|------------------|-----------------------------------------------------------------------------------|
| App (UI)         | http://localhost:5188                                                             |
| API docs, Scalar | http://localhost:5188/scalar                                                      |
| OpenAPI document | http://localhost:5188/openapi/v1.json                                             |
| Aspire dashboard | http://localhost:15188 — open the login link with `?t=…` that the command prints |

The database has no volume, so **every run starts from a freshly migrated and seeded database**, and edits don't
survive a restart.

## Seed size

The sample tree is generated with Bogus from a fixed random seed, so it has the same shape and the same ids on
every machine. Default: 100,000 elements. Allowed range: 50,000 to 1,000,000.

Set the size in `src/TreeEditor.AppHost/appsettings.json` → `Parameters:seed-size`, or override it for one run:

```bash
aspire run -- --Parameters:seed-size=1000000
```

| Size | Seeding (MigrationService) | API up after `aspire run` | Reset |
|------|----------------------------|---------------------------|-------|
| 100k | ~4 s                       | ~20 s                     | ~1.5 s |
| 1M   | ~26 s                      | ~45 s                     | ~16 s  |

These times were measured on an Apple Silicon laptop.

The tree is generated once into a `seed_nodes` table with binary `COPY`, and `nodes` is filled from that table.
The MigrationService regenerates `seed_nodes` only when its row count differs from the configured size; then it
refills `nodes`, which wipes any edits. With the same size, the stored seed is reused, and `nodes` is filled only
when it is empty. Because the database is ephemeral, in practice every `aspire run` seeds from scratch.

Shape: 5 roots. A chain 200 levels deep. 3 wide elements with 5k–10k children each. Everything else is at most
12 levels deep, with 0–20 children per element. Sibling name clashes get a ` (n)` suffix.

## Schema

One table, `nodes`: an adjacency list plus the materialized ancestor path.

| Column       | Type           | Notes                                                                        |
|--------------|----------------|------------------------------------------------------------------------------|
| `id`         | `uuid` PK      | GUID v7. New elements get their final id in the browser, so there is no temp-id mapping. |
| `parent_id`  | `uuid` null    | FK → `id`. `null` means a root.                                             |
| `ancestors`  | `uuid[]`       | Root first, own id last. Computed by the server from the parent. Written once, because elements never move. |
| `value`      | `varchar(255)` | Trimmed, required.                                                           |
| `is_deleted` | `bool`         | Soft delete. Deleted elements stay visible (struck through) and can't be edited. |
| `xmin`       | system column  | Row version for optimistic concurrency. Postgres bumps it on every update.   |

Indexes:

- `ix_nodes_ancestors`, GIN on `ancestors`:
  - `ancestors @> ARRAY[x]` finds a whole subtree at any depth in one indexed query;
  - a delete therefore cascades to descendants that were never loaded;
  - the root of any element is `ancestors[1]`.
- `ix_nodes_parent_id_lower_value_id` on `(parent_id, lower(value), id)`: children in alphabetical order, with
  keyset paging (100 per page; the query reads 101 rows to tell whether there are more).
- `ux_nodes_parent_id_lower_value`, `UNIQUE (parent_id, lower(value)) NULLS NOT DISTINCT WHERE NOT is_deleted`:
  - no two live siblings share a name, compared case-insensitively;
  - roots are siblings too;
  - deleted elements free their name.

`seed_nodes` has the same columns without `xmin`. Reset copies from it.

Why arrays and not `ltree`, a recursive CTE, nested sets or a closure table: see
[Data model](docs/DECISIONS.md#data-model). In short, `ltree` + GiST fails at the 200-level chain, and elements
never move, so paths are never rewritten.

## Key decisions

### Local cache (browser)

- Lives in the Blazor WebAssembly app, and the API stays stateless.
- Talks to the database through exactly two calls:
  - `GET /api/nodes/{id}` (load an element);
  - `POST /api/apply`.
- Everything else happens in memory: edits, the delete cascade among cached elements, and the local ` (n)` suffix.
- Ancestors that aren't cached show as placeholder rows ("… 2 levels not loaded"), so every element sits at its
  real depth.
- Nothing is persisted. Leaving the page with pending changes asks for confirmation, and "Discard all changes"
  drops them all.

The Database tree reads through `GET /api/nodes/children?parentId=&afterValue=&afterId=`. Without `parentId` it
returns the roots. Each page holds 100 children and a `next` cursor.

### Apply

`POST /api/apply` with `{ inserts: [{id, parentId, value}], edits: [{id, value, version}], deletes: [{id, version}] }`.
It is all-or-nothing, and the steps run in this order:

1. Validate the request. Invalid values, an id used twice, or new parents that loop back instead of reaching a
   stored element → 400.
2. Work out the touched root trees from the stored rows, before the transaction; an element's root never changes.
3. `BEGIN`, then take advisory locks on those roots (see [Concurrency](#concurrency)).
4. An insert whose id already exists → 400.
5. Conflict check. If anything conflicts → 409, and nothing is written.
6. Write, one at a time:
   - inserts, parents first; `ancestors` comes from the stored parent;
   - edits, with `xmin` as the concurrency token;
   - deletes last, as one `UPDATE … WHERE ancestors @> ARRAY[id]` that soft-deletes the whole subtree.
   - A clashing sibling name gets the first free ` (n)` suffix, decided by the server, which sees every sibling.
7. `COMMIT`, then invalidate the read cache for the touched roots.

The response is 200 `{ nodes: [{id, value, version, isDeleted}] }`: the final values and new versions.

### Conflicts

A 409 is a ProblemDetails with a `conflicts` array:

```json
{ "status": 409, "title": "The changes conflict with the database; nothing was applied.",
  "conflicts": [
    { "id": "…", "reason": "VersionChanged", "value": "current DB value", "version": 770, "isDeleted": false },
    { "id": "…", "reason": "Deleted", "value": null, "version": null, "isDeleted": true } ] }
```

- `VersionChanged` is returned when an edited or deleted element changed in the database since it was loaded. The
  element shows the database value with two buttons:
  - **Take DB** drops the local change;
  - **Keep mine** rebases the change onto the new version, to be applied again.
- `Deleted` is returned when the element, or the parent of a new element, was deleted in the database (value and
  version are `null` if the row is missing). It resolves itself:
  - the element becomes deleted in the cache, along with its cached descendants;
  - their pending changes are dropped;
  - new children under it are removed.
- **Apply** stays disabled until every conflict is resolved, and while a request is in flight.

### Concurrency

- The version check catches stale data. It does not catch two Applies whose transactions overlap: for example, a
  delete that doesn't see a child inserted concurrently, or two Applies that pick the same name suffix.
- So Applies on the same root tree run one at a time. Each takes `pg_advisory_xact_lock` per touched root:
  - the key is the root uuid folded to 64 bits;
  - the locks are taken in sorted order, which prevents deadlocks;
  - they are released on commit or rollback.
- Applies on different roots run in parallel.
- Roots are siblings of each other, so an Apply that renames a root also takes one shared "roots" lock.
- Reset needs no extra lock: `TRUNCATE` waits for running Applies and blocks new ones.

### Backend read cache

- Uses `HybridCache`, in-memory, for children pages and element loads, with stampede protection.
- Entries expire after 60 s. Every entry is tagged `root:{id}` with the root of what it shows, and the roots listing
  is also tagged `roots`.
- After a commit, Apply removes the touched roots' tags. Unknown ids are not cached.
- Scaling out needs no code change:
  - add `builder.AddRedis("cache")` in the AppHost;
  - reference it from the API with `.WithReference(cache)`;
  - call `builder.AddRedisDistributedCache("cache")` in the API.
  - HybridCache then uses Redis as its L2.

### Reset

- Toolbar **Reset** asks for confirmation, then calls `POST /api/reset` (204).
- The server runs one transaction: `TRUNCATE nodes`, then `INSERT … SELECT` from `seed_nodes`, then `ANALYZE`.
- After the commit, the whole read cache is cleared.
- The browser clears the local cache and reloads the Database tree.
- Other tabs get conflicts on their next Apply.

## Demo elements

Ids are the same on every machine at every seed size. Use them in Scalar, or look for the names in the UI.

| Element                                  | Id                                     | Notes                         |
|------------------------------------------|----------------------------------------|-------------------------------|
| Root "Computers"                         | `019b76da-a804-7cc0-9ad7-6b37f6d4184d` |                               |
| Root "Electronics"                       | `019b76da-a802-70b7-b288-4b83d0e0c0a1` |                               |
| Root "Garden"                            | `019b76da-a803-7b84-87fc-49be8a32415c` |                               |
| Root "Home"                              | `019b76da-a801-7926-9a3c-17b775163b37` |                               |
| Root "Industrial"                        | `019b76da-a800-709f-b3bf-d4c669178afe` | holds the deep chain          |
| Chain start "Unbranded Soft Chicken"     | `019b76da-a805-73fe-9941-734233ec6816` | level 2; below it "Level 3 …", "Level 4 …" |
| Chain "Level 100 Refined Concrete Soap"  | `019b76da-a87a-73d9-a32d-7103d3734e83` | level 100                     |
| Chain end "Level 200 Refined Plastic Cheese" | `019b76da-a8de-7489-9cc7-0abfe336c739` | level 200, leaf           |
| Wide "Fantastic Wooden Keyboard" (Home)  | `019b76da-a8df-75ee-af0f-faac623e41b7` | 9,626 children                |
| Wide "Awesome Plastic Salad" (Electronics) | `019b76da-ce89-71c3-b560-7467c764547b` | 6,314 children              |
| Wide "Rustic Wooden Chips" (Garden)      | `019b76da-e743-7cee-a911-3fc8424eaea6` | 5,788 children                |

Things to try:

- **Placeholders.** Load Industrial into the cache, then load "Level 4 …" from under the chain start. The Cache
  shows "… 2 levels not loaded" between them.
- **Paging.** Expand a wide element, then use "Load 100 more".
- **Cascade delete.** Delete an element in the cache and Apply. Its unloaded descendants appear struck through in
  the Database tree.
- **Conflicts.** Load the same element into the cache in two tabs, rename it in both, and Apply both.

## Tests

```bash
dotnet test --solution TreeEditor.slnx
```

Docker must be running. The test projects are:

- Domain and Web: unit tests of the tree rules and the local cache.
- MigrationService: the seed shape and ids.
- Api: integration tests on Testcontainers PostgreSQL, covering apply, cascade, conflicts, locks, racing Applies,
  paging, the read cache and Reset.
- AppHost: a smoke test that starts the whole app through `Aspire.Hosting.Testing`.

## Known limitations

- **Two concurrent Applies inserting the same new id under different root trees.** They don't share a lock, so the
  second one hits the primary key and gets a 500 instead of a 400. The UI generates fresh GUIDs, so only a
  hand-made request can do this.
- **Ids stored after the touched roots are read.** The roots are read before `BEGIN`. An id that another Apply
  stores after that read takes no lock. The version check still applies.
- **Lock waits.** A wait for an advisory lock is bounded by the 30 s command timeout; after that the request returns
  500.
- **Sort order in the Cache tree.** The Cache tree sorts with ordinal `ToLowerInvariant`, while the database uses
  Postgres `lower()` and the collation. Names with non-ASCII characters or punctuation can be ordered differently
  in the two trees.
- **Stale read cache.** A read racing a commit can cache stale data for up to 60 s. The worst case is a false
  conflict on the next Apply.
- **Single API instance.** The read cache is in-memory (see the Redis note above), and the database is ephemeral.
- **First-start log noise.** The first start logs one EF `fail` for the query on `__EFMigrationsHistory`, before the
  table exists. It is harmless.
- **Out of scope:** authentication, an "add root" action, and persisting the cache across a page refresh.
