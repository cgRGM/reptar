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
