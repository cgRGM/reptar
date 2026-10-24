import type * as React from "react";

import { cn } from "../lib/utils";

export interface ButtonProps extends React.ButtonHTMLAttributes<HTMLButtonElement> {
  variant?:
    | "default"
    | "destructive"
    | "outline"
    | "secondary"
    | "ghost"
    | "link";
  size?: "default" | "sm" | "lg" | "icon";
}

const variantStyles: Record<NonNullable<ButtonProps["variant"]>, string> = {
  default: "bg-primary text-primary-foreground shadow hover:bg-primary/90",
  destructive:
    "bg-destructive text-destructive-foreground shadow-sm hover:bg-destructive/90",
  ghost: "hover:bg-accent hover:text-accent-foreground",
  link: "text-primary underline-offset-4 hover:underline",
  outline:
    "border border-input bg-background shadow-sm hover:bg-accent hover:text-accent-foreground",
  secondary:
    "bg-secondary text-secondary-foreground shadow-sm hover:bg-secondary/80",
};

const sizeStyles: Record<NonNullable<ButtonProps["size"]>, string> = {
  default: "h-9 px-4 py-2",
  icon: "h-9 w-9",
  lg: "h-10 rounded-md px-8",
  sm: "h-8 rounded-md px-3 text-xs",
};

export const Button = ({
  className,
  variant = "default",
  size = "default",
  disabled,
  children,
  ...props
}: ButtonProps): React.JSX.Element => (
  <button
    className={cn(
      "focus-visible:ring-ring inline-flex items-center justify-center gap-2 rounded-md text-sm font-medium whitespace-nowrap transition-colors focus-visible:ring-1 focus-visible:outline-none disabled:pointer-events-none disabled:opacity-50 [&_svg]:pointer-events-none [&_svg]:size-4 [&_svg]:shrink-0",
      variantStyles[variant],
      sizeStyles[size],
      className
    )}
    disabled={disabled}
    {...props}
  >
    {children}
  </button>
);
