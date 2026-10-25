-- Composite indexes for the RealWorld feed and follow lookups.
create index if not exists rw_follows_follower_followed_idx
  on rw_follows(follower_id, followed_id);
create index if not exists rw_articles_author_created_idx
  on rw_articles(author_id, created_at);
