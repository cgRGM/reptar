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

create table if not exists rw_users (
  id text primary key,
  username text not null unique,
  email text not null unique,
  password_hash text not null,
  bio text,
  image text,
  created_at text not null
);

create table if not exists rw_articles (
  id text primary key,
  author_id text not null,
  slug text not null unique,
  title text not null,
  description text not null,
  body text not null,
  created_at text not null,
  updated_at text not null
);

create index if not exists rw_articles_author_idx on rw_articles(author_id);
create index if not exists rw_articles_created_idx on rw_articles(created_at);

create table if not exists rw_tags (
  id text primary key,
  name text not null unique
);

create table if not exists rw_article_tags (
  id text primary key,
  article_id text not null,
  tag_id text not null
);

create index if not exists rw_article_tags_article_idx on rw_article_tags(article_id);
create index if not exists rw_article_tags_tag_idx on rw_article_tags(tag_id);

create table if not exists rw_comments (
  id text primary key,
  article_id text not null,
  author_id text not null,
  body text not null,
  created_at text not null,
  updated_at text not null
);

create index if not exists rw_comments_article_idx on rw_comments(article_id);

create table if not exists rw_favorites (
  id text primary key,
  article_id text not null,
  user_id text not null,
  created_at text not null
);

create index if not exists rw_favorites_article_idx on rw_favorites(article_id);
create index if not exists rw_favorites_user_idx on rw_favorites(user_id);

create table if not exists rw_follows (
  id text primary key,
  follower_id text not null,
  followed_id text not null,
  created_at text not null
);

create index if not exists rw_follows_follower_idx on rw_follows(follower_id);
create index if not exists rw_follows_followed_idx on rw_follows(followed_id);
create index if not exists rw_article_tags_article_tag_idx on rw_article_tags(article_id, tag_id);
create index if not exists rw_favorites_article_user_idx on rw_favorites(article_id, user_id);
create index if not exists rw_follows_follower_followed_idx on rw_follows(follower_id, followed_id);
