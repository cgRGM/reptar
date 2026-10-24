alter table posts add column author_id text not null default 'anonymous';
alter table posts add column slug text not null default '';
alter table posts add column excerpt text;
alter table posts add column published integer not null default 1;
alter table posts add column updated_at text not null default '';
create index if not exists posts_slug_idx on posts(slug);
