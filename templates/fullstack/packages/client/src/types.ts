export interface Item {
  id: string;
  userId: string;
  title: string;
  content: string;
  createdAt: string;
}

export interface CreateItemInput {
  title: string;
  content: string;
}

export interface User {
  id: string;
  name: string;
  email: string;
  image?: string | null;
  role: string;
  createdAt?: string;
}
