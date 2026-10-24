create table if not exists posts (
  id text primary key,
  author_id text not null default 'anonymous',
  slug text not null default '',
  title text not null,
  content text not null,
  excerpt text,
  published integer not null default 1,
  created_at text not null,
  updated_at text not null default '',
  image_key text
);

create index if not exists posts_slug_idx on posts(slug);

create table if not exists users (
  id text primary key,
  email text not null unique,
  name text not null,
  password_hash text not null,
  is_anonymous integer not null default 0,
  role text not null default 'user',
  created_at text not null
);

create table if not exists sessions (
  id text primary key,
  user_id text not null references users(id) on delete cascade,
  token_hash text not null unique,
  expires_at text not null,
  created_at text not null
);

create index if not exists sessions_token_hash_idx on sessions(token_hash);
