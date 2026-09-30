# Tree Editor

Two trees side by side: **Database** (DBTreeView) reads a large PostgreSQL tree lazily; **Cache** (CachedTreeView)
edits loaded elements in the browser and saves them with **Apply**. Stack: .NET 10, Aspire 13, PostgreSQL 18,
EF Core 10, Blazor WebAssembly (served by the API), MudBlazor.

## Run

Prerequisites: [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), Docker (running), Aspire CLI
(`curl -sSL https://aspire.dev/install.sh | bash`; Windows: `irm https://aspire.dev/install.ps1 | iex`).

```bash
aspire run   # from the repo root
```

This starts PostgreSQL, migrates and seeds it, then starts the API, which also serves the UI (about 20 s once the
Postgres image is pulled). The database has no volume, so every run starts from fresh sample data.

- UI: http://localhost:5188 · API docs (Scalar): http://localhost:5188/scalar
- Aspire dashboard: http://localhost:15188 (open the login link with `?t=…` that `aspire run` prints)

If `dotnet --version` shows less than 10 (e.g. the SDK is in `~/.dotnet`), run `export PATH="$HOME/.dotnet:$PATH"` first.

Seed size: 100,000 elements by default, 50,000 to 1,000,000. Set `Parameters:seed-size` in
`src/TreeEditor.AppHost/appsettings.json`, or for one run: `aspire run -- --Parameters:seed-size=1000000`.

## Assignment requirements

- **DBTreeView shows the DB tree without loading it all:** one level per expand, 100 children per page
  ("Load 100 more"), keyset paging on `(lower(value), id)`.
- **CachedTreeView loads elements one by one, in any order, and related ones nest correctly:** each element comes with
  its ancestor ids, so it sits at its real depth; missing levels in between show as a placeholder row
  ("… 2 levels not loaded").
- **Edit, add child and delete stay pending in the cache until Apply.** "Discard all changes" drops them.
- **The cache talks to the server through two calls only:** load element (`GET /api/nodes/{id}`) and apply
  (`POST /api/apply`).
- **Delete cascades to all descendants, including never-loaded ones:** Apply soft-deletes the subtree in one
  `UPDATE … WHERE ancestors @> ARRAY[id]`. Deleted elements are struck through and can't be edited.
- **Reset restores the sample data:** refills `nodes` from a stored copy of the seed in one transaction, then clears
  the caches and reloads the trees.
- **Sample data has at least 4 levels:** 100k elements by default from a fixed random seed (same ids everywhere):
  5 roots, a 200-level chain, 3 elements with 5k–10k children, the rest up to 12 levels deep.

## Schema

One table, `nodes` (`seed_nodes` has the same columns; Reset copies from it):

| Column       | Type           | Notes                                  |
|--------------|----------------|----------------------------------------|
| `id`         | `uuid` PK      | UUID v7                                |
| `parent_id`  | `uuid` null    | FK → `id`; `null` = root               |
| `ancestors`  | `uuid[]`       | root first, own id last; GIN index     |
| `value`      | `varchar(255)` | trimmed, required                      |
| `is_deleted` | `bool`         | soft delete                            |
| `xmin`       | system column  | row version for optimistic concurrency |

- Adjacency list for "children of X"; a btree index on `(parent_id, lower(value), id)` also gives the paging order.
- `ancestors` + GIN finds a whole subtree at any depth in one indexed query (`ancestors @> ARRAY[x]`). Elements never
  move, so the array is written once. (`ltree` + GiST fails on the 200-level chain.)
- Soft delete keeps deleted elements visible. Postgres bumps `xmin` on every update, so no version column is needed.
- `UNIQUE (parent_id, lower(value)) NULLS NOT DISTINCT WHERE NOT is_deleted`: case-insensitively unique names among
  live siblings, roots included.

Reasons and rejected options: [docs/DECISIONS.md](docs/DECISIONS.md).

## Implementation decisions

- **Apply is one all-or-nothing transaction:** inserts (parents first), then edits, then deletes.
- **Conflicts with other tabs are listed at once** (409, nothing written). Each changed element shows the DB value
  with **Take DB** / **Keep mine**; one deleted elsewhere just becomes deleted in the cache.
- **Per-root advisory locks:** Applies on the same root tree run one at a time; different trees save in parallel.
- **New element ids are generated in the browser (UUID v7),** so children of new elements work before Apply.
- **Duplicate sibling names get a " (1)" suffix** (the first free ` (n)`), decided by the server, which sees all
  siblings.
- **Backend read cache:** HybridCache, in-memory, invalidated per root tree after each Apply; Redis-ready (add it
  in the AppHost, no code changes).

## Demo elements

Ids are the same on every machine and seed size. Use them in Scalar, or find the names in the UI.

| Element                                      | Id                                     | Hint                        |
|----------------------------------------------|----------------------------------------|-----------------------------|
| Root "Industrial"                            | `019b76da-a800-709f-b3bf-d4c669178afe` | holds the 200-level chain   |
| Chain start "Unbranded Soft Chicken"         | `019b76da-a805-73fe-9941-734233ec6816` | level 2; cache Industrial and "Level 4 …" to see a placeholder |
| Chain end "Level 200 Refined Plastic Cheese" | `019b76da-a8de-7489-9cc7-0abfe336c739` | deepest element (level 200) |
| Wide "Fantastic Wooden Keyboard" (Home)      | `019b76da-a8df-75ee-af0f-faac623e41b7` | 9,626 children; paging      |

## Tests

`dotnet test --solution TreeEditor.slnx` (Docker must be running: integration tests use Testcontainers PostgreSQL).
