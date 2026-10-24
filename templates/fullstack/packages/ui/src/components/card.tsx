import type * as React from "react";

import { cn } from "../lib/utils";

export const Card = ({
  className,
  children,
  ...props
}: React.HTMLAttributes<HTMLDivElement>): React.JSX.Element => (
  <div
    className={cn(
      "bg-card text-card-foreground rounded-xl border shadow",
      className
    )}
    {...props}
  >
    {children}
  </div>
);

export const CardHeader = ({
  className,
  children,
  ...props
}: React.HTMLAttributes<HTMLDivElement>): React.JSX.Element => (
  <div className={cn("flex flex-col space-y-1.5 p-6", className)} {...props}>
    {children}
  </div>
);

export const CardTitle = ({
  className,
  children,
  ...props
}: React.HTMLAttributes<HTMLHeadingElement>): React.JSX.Element => (
  <h3
    className={cn("leading-none font-semibold tracking-tight", className)}
    {...props}
  >
    {children}
  </h3>
);

export const CardDescription = ({
  className,
  children,
  ...props
}: React.HTMLAttributes<HTMLParagraphElement>): React.JSX.Element => (
  <p className={cn("text-muted-foreground text-sm", className)} {...props}>
    {children}
  </p>
);

export const CardContent = ({
  className,
  children,
  ...props
}: React.HTMLAttributes<HTMLDivElement>): React.JSX.Element => (
  <div className={cn("p-6 pt-0", className)} {...props}>
    {children}
  </div>
);

export const CardFooter = ({
  className,
  children,
  ...props
}: React.HTMLAttributes<HTMLDivElement>): React.JSX.Element => (
  <div className={cn("flex items-center p-6 pt-0", className)} {...props}>
    {children}
  </div>
);
