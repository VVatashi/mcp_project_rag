-- The server runs this file once, on an empty volume.
-- The migrations of the application also create the extensions. This file lets the application connect as a user without superuser rights.

-- pgvector: the vector and halfvec types and the HNSW index.
CREATE EXTENSION IF NOT EXISTS vector;

-- pg_trgm: the fuzzy search of document paths and titles.
CREATE EXTENSION IF NOT EXISTS pg_trgm;
