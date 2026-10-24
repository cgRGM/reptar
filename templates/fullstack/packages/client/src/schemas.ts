import { z } from "zod";

export const ItemSchema = z.object({
  content: z.string(),
  createdAt: z.string(),
  id: z.string(),
  title: z.string(),
  userId: z.string(),
});

export const CreateItemInputSchema = z.object({
  content: z.string().min(1, "Content is required"),
  title: z.string().min(1, "Title is required"),
});

export const UserSchema = z.object({
  createdAt: z.string().optional(),
  email: z.string().email(),
  id: z.string(),
  image: z.string().nullable().optional(),
  name: z.string(),
  role: z.string(),
});
