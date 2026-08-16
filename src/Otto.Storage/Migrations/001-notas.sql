-- Historial de dictados. Cada dictado es una nota: no hay un paso aparte de
-- "guardar esto", así que esta tabla se escribe sola en cada uso.

CREATE TABLE notes (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    title         TEXT    NOT NULL DEFAULT '',
    text          TEXT    NOT NULL,
    created_at    TEXT    NOT NULL,
    updated_at    TEXT    NOT NULL,
    process_name  TEXT    NOT NULL DEFAULT '',
    window_title  TEXT    NOT NULL DEFAULT '',
    audio_seconds REAL    NOT NULL DEFAULT 0
);

CREATE INDEX ix_notes_created_at ON notes (created_at DESC);

-- FTS5 en modo "external content": el índice referencia la tabla notes en vez de
-- duplicar el texto. Con remove_diacritics buscar "recien" encuentra "recién",
-- que en castellano es lo que la gente espera.
CREATE VIRTUAL TABLE notes_fts USING fts5(
    title,
    text,
    content='notes',
    content_rowid='id',
    tokenize="unicode61 remove_diacritics 2"
);

-- El contenido externo no se sincroniza solo: hay que mantenerlo con triggers.
-- Los de borrado y actualización usan la fila especial 'delete' para sacar del
-- índice la versión vieja antes de insertar la nueva.
CREATE TRIGGER notes_fts_insert AFTER INSERT ON notes BEGIN
    INSERT INTO notes_fts (rowid, title, text) VALUES (new.id, new.title, new.text);
END;

CREATE TRIGGER notes_fts_delete AFTER DELETE ON notes BEGIN
    INSERT INTO notes_fts (notes_fts, rowid, title, text) VALUES ('delete', old.id, old.title, old.text);
END;

CREATE TRIGGER notes_fts_update AFTER UPDATE ON notes BEGIN
    INSERT INTO notes_fts (notes_fts, rowid, title, text) VALUES ('delete', old.id, old.title, old.text);
    INSERT INTO notes_fts (rowid, title, text) VALUES (new.id, new.title, new.text);
END;
