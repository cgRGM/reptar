import * as React from "react";

import { cn } from "../lib/utils";

interface DropdownContextType {
  open: boolean;
  setOpen: React.Dispatch<React.SetStateAction<boolean>>;
}

const defaultSetOpen: React.Dispatch<React.SetStateAction<boolean>> = () => {
  // initial context placeholder
};

const DropdownContext = React.createContext<DropdownContextType>({
  open: false,
  setOpen: defaultSetOpen,
});

export const DropdownMenu = ({
  children,
}: {
  children: React.ReactNode;
}): React.JSX.Element => {
  const [open, setOpen] = React.useState(false);
  const ref = React.useRef<HTMLDivElement>(null);

  React.useEffect(() => {
    const handleClickOutside = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) {
        setOpen(false);
      }
    };
    if (open) {
      document.addEventListener("mousedown", handleClickOutside);
    }
    return () => {
      document.removeEventListener("mousedown", handleClickOutside);
    };
  }, [open]);

  return (
    <DropdownContext.Provider value={{ open, setOpen }}>
      <div ref={ref} className="relative inline-block text-left">
        {children}
      </div>
    </DropdownContext.Provider>
  );
};

export const DropdownMenuTrigger = ({
  children,
  className,
}: {
  children: React.ReactNode;
  className?: string;
}): React.JSX.Element => {
  const { open, setOpen } = React.useContext(DropdownContext);
  return (
    <button
      type="button"
      onClick={() => setOpen(!open)}
      aria-expanded={open}
      className={cn("inline-flex items-center", className)}
    >
      {children}
    </button>
  );
};

export const DropdownMenuContent = ({
  children,
  className,
  align = "end",
}: {
  children: React.ReactNode;
  className?: string;
  align?: "start" | "end";
}): React.JSX.Element | null => {
  const { open } = React.useContext(DropdownContext);
  if (!open) {
    return null;
  }

  return (
    <div
      role="menu"
      tabIndex={-1}
      className={cn(
        "bg-popover text-popover-foreground absolute z-50 mt-2 min-w-[8rem] overflow-hidden rounded-md border p-1 shadow-md transition-all focus:outline-none",
        align === "end" ? "right-0" : "left-0",
        className
      )}
    >
      {children}
    </div>
  );
};

export const DropdownMenuItem = ({
  children,
  onClick,
  className,
}: {
  children: React.ReactNode;
  onClick?: () => void;
  className?: string;
}): React.JSX.Element => {
  const { setOpen } = React.useContext(DropdownContext);
  return (
    <button
      type="button"
      role="menuitem"
      tabIndex={0}
      onClick={() => {
        onClick?.();
        setOpen(false);
      }}
      className={cn(
        "hover:bg-accent hover:text-accent-foreground focus:bg-accent focus:text-accent-foreground relative flex w-full cursor-default items-center rounded-sm px-2 py-1.5 text-sm transition-colors outline-none select-none",
        className
      )}
    >
      {children}
    </button>
  );
};

export const DropdownMenuSeparator = ({
  className,
}: {
  className?: string;
}): React.JSX.Element => (
  <div className={cn("bg-muted -mx-1 my-1 h-px", className)} />
);
