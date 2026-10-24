import type * as React from "react";
import { useEffect, useState } from "react";
import { useNavigate } from "react-router";

import { SignInForm } from "../components/sign-in-form";
import { SignUpForm } from "../components/sign-up-form";
import { useSession } from "../lib/auth-client";

export const LoginRoute = (): React.JSX.Element => {
  const [isSignUp, setIsSignUp] = useState(false);
  const { data: session, isPending } = useSession();
  const navigate = useNavigate();

  useEffect(() => {
    if (session?.user && !isPending) {
      navigate("/dashboard");
    }
  }, [session, isPending, navigate]);

  return (
    <div className="flex min-h-[calc(100vh-8rem)] items-center justify-center p-4">
      {isSignUp ? (
        <SignUpForm onSwitchToSignIn={() => setIsSignUp(false)} />
      ) : (
        <SignInForm onSwitchToSignUp={() => setIsSignUp(true)} />
      )}
    </div>
  );
};
