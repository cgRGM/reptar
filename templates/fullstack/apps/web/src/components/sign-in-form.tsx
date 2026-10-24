import {
  Button,
  Card,
  CardContent,
  CardDescription,
  CardFooter,
  CardHeader,
  CardTitle,
  Input,
  Label,
} from "@{{PROJECT_NAME}}/ui";
import type * as React from "react";
import { useState } from "react";
import { useNavigate } from "react-router";

import { authClient } from "../lib/auth-client";

interface SignInFormProps {
  onSwitchToSignUp?: () => void;
}

export const SignInForm = ({
  onSwitchToSignUp,
}: SignInFormProps): React.JSX.Element => {
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const navigate = useNavigate();

  const handleEmailSignIn = async (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);
    setLoading(true);

    try {
      const res = await authClient.signIn.email({
        email,
        password,
      });

      if (res.error) {
        setError(res.error.message ?? "Invalid email or password");
      } else {
        navigate("/dashboard");
      }
    } catch (error_: unknown) {
      setError(error_ instanceof Error ? error_.message : "Failed to sign in");
    } finally {
      setLoading(false);
    }
  };

  const handleSocialSignIn = async (provider: "google" | "github") => {
    try {
      await authClient.signIn.social({
        callbackURL: "/dashboard",
        provider,
      });
    } catch (error_: unknown) {
      setError(
        error_ instanceof Error
          ? error_.message
          : `Failed to sign in with ${provider}`
      );
    }
  };

  return (
    <Card className="mx-auto w-full max-w-md">
      <CardHeader className="space-y-1 text-center">
        <CardTitle className="text-2xl font-bold">Sign In</CardTitle>
        <CardDescription>
          Enter your email and password to access your account
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-4">
        {error ? (
          <div className="bg-destructive/15 text-destructive rounded-md p-3 text-sm">
            {error}
          </div>
        ) : null}

        <form onSubmit={handleEmailSignIn} className="space-y-4">
          <div className="space-y-2">
            <Label htmlFor="signin-email">Email</Label>
            <Input
              id="signin-email"
              type="email"
              placeholder="you@example.com"
              value={email}
              onChange={(e) => setEmail(e.target.value)}
              required
            />
          </div>
          <div className="space-y-2">
            <Label htmlFor="signin-password">Password</Label>
            <Input
              id="signin-password"
              type="password"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              required
            />
          </div>
          <Button type="submit" disabled={loading} className="w-full">
            {loading ? "Signing in..." : "Sign in with Email"}
          </Button>
        </form>

        <div className="relative">
          <div className="absolute inset-0 flex items-center">
            <div className="border-border w-full border-t" />
          </div>
          <div className="relative flex justify-center text-xs uppercase">
            <span className="bg-card text-muted-foreground px-2">
              Or continue with
            </span>
          </div>
        </div>

        <div className="grid grid-cols-2 gap-3">
          <Button
            type="button"
            variant="outline"
            onClick={() => handleSocialSignIn("google")}
            className="w-full"
          >
            Google
          </Button>
          <Button
            type="button"
            variant="outline"
            onClick={() => handleSocialSignIn("github")}
            className="w-full"
          >
            GitHub
          </Button>
        </div>
      </CardContent>
      <CardFooter className="border-border flex justify-center border-t pt-4">
        <p className="text-muted-foreground text-xs">
          Don&apos;t have an account?{" "}
          <button
            type="button"
            onClick={onSwitchToSignUp}
            className="text-primary hover:text-primary/80 underline"
          >
            Sign up
          </button>
        </p>
      </CardFooter>
    </Card>
  );
};
