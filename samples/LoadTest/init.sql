-- The destination of the load test: one row per page. A missing row is a lost page; writes above 1 is a page written twice.
CREATE SCHEMA loadtest;

CREATE TABLE loadtest.pages (
    owner        text        NOT NULL,
    window_start timestamptz NOT NULL,
    page         integer     NOT NULL,
    writes       integer     NOT NULL DEFAULT 1,
    instance     text        NOT NULL,
    written_at   timestamptz NOT NULL,
    PRIMARY KEY (owner, window_start, page)
);
