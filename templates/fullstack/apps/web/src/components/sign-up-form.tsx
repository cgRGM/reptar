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

interface SignUpFormProps {
  onSwitchToSignIn?: () => void;
}

export const SignUpForm = ({
  onSwitchToSignIn,
}: SignUpFormProps): React.JSX.Element => {
  const [name, setName] = useState("");
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const navigate = useNavigate();

  const handleSignUp = async (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);
    setLoading(true);

    try {
      const res = await authClient.signUp.email({
        email,
        name,
        password,
      });

      if (res.error) {
        setError(res.error.message ?? "Failed to create account");
      } else {
        navigate("/dashboard");
      }
    } catch (error_: unknown) {
      setError(error_ instanceof Error ? error_.message : "Failed to sign up");
    } finally {
      setLoading(false);
    }
  };

  return (
    <Card className="mx-auto w-full max-w-md">
      <CardHeader className="space-y-1 text-center">
        <CardTitle className="text-2xl font-bold">Create an Account</CardTitle>
        <CardDescription>
          Enter your details below to get started with {{ PROJECT_TITLE }}
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-4">
        {error ? (
          <div className="bg-destructive/15 text-destructive rounded-md p-3 text-sm">
            {error}
          </div>
        ) : null}

        <form onSubmit={handleSignUp} className="space-y-4">
          <div className="space-y-2">
            <Label htmlFor="signup-name">Full Name</Label>
            <Input
              id="signup-name"
              type="text"
              placeholder="Alex Smith"
              value={name}
              onChange={(e) => setName(e.target.value)}
              required
            />
          </div>
          <div className="space-y-2">
            <Label htmlFor="signup-email">Email</Label>
            <Input
              id="signup-email"
              type="email"
              placeholder="you@example.com"
              value={email}
              onChange={(e) => setEmail(e.target.value)}
              required
            />
          </div>
          <div className="space-y-2">
            <Label htmlFor="signup-password">Password</Label>
            <Input
              id="signup-password"
              type="password"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              required
            />
          </div>
          <Button type="submit" disabled={loading} className="w-full">
            {loading ? "Creating account..." : "Sign Up"}
          </Button>
        </form>
      </CardContent>
      <CardFooter className="border-border flex justify-center border-t pt-4">
        <p className="text-muted-foreground text-xs">
          Already have an account?{" "}
          <button
            type="button"
            onClick={onSwitchToSignIn}
            className="text-primary hover:text-primary/80 underline"
          >
            Sign in
          </button>
        </p>
      </CardFooter>
    </Card>
  );
};
