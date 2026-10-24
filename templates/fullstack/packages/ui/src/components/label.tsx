import type * as React from "react";

import { cn } from "../lib/utils";

export type LabelProps = React.LabelHTMLAttributes<HTMLLabelElement>;

export const Label = ({
  className,
  children,
  ...props
}: LabelProps): React.JSX.Element => (
  <label
    className={cn(
      "text-sm leading-none font-medium peer-disabled:cursor-not-allowed peer-disabled:opacity-70",
      className
    )}
    {...props}
  >
    {children}
  </label>
);
