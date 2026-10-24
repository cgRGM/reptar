export interface CreatePost {
  title: string;
  content: string;
  imageKey?: string | null;
  excerpt?: string | null;
}

export interface ErrorBody {
  code: string;
  message: string;
  requestId: string | null;
}

export interface Post {
  id: string;
  title: string;
  content: string;
  imageKey?: string | null;
  createdAt: string;
  authorId?: string;
  slug?: string;
  published?: number;
  updatedAt?: string;
}

export interface UploadResult {
  key: string;
  url: string;
}

export interface SessionResponse {
  user: SessionUser | null;
}

export interface SessionUser {
  id: string;
  email: string;
  name: string;
}

export interface SignInInput {
  email: string;
  password: string;
}

export interface SignUpInput {
  email: string;
  name: string;
  password: string;
}

export interface UpdatePost {
  content?: string | null;
  excerpt?: string | null;
  imageKey?: string | null;
  title?: string | null;
}

export interface ValidationError {
  code: string;
  issues: ValidationIssue[];
  message: string;
  requestId: string | null;
}

export interface ValidationIssue {
  message: string;
  path: string[] | null;
}

export interface StripeLineItem {
  currency?: string;
  name: string;
  quantity?: number;
  unitAmountInCents: number;
}

export interface CreateCheckoutInput {
  cancelUrl: string;
  clientReferenceId?: string | null;
  customerEmail?: string | null;
  lineItems: StripeLineItem[];
  mode?: string;
  successUrl: string;
}

export interface CheckoutSession {
  id: string;
  status: string;
  url: string;
}
