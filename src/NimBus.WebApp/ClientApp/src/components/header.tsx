import { useState } from "react";
import { Link as ReactLink } from "react-router-dom";
import { Badge } from "components/ui/badge";
import { getEnv } from "hooks/app-status";
import { useTheme } from "hooks/use-theme";
import { Navigation } from "models/navigation";

const MenuItem: React.FC<{ children: React.ReactNode }> = ({ children }) => (
  <span className="block mr-6 p-1.5 md:mt-0 mt-4">{children}</span>
);

interface HeaderProps {
  links: Navigation;
  className?: string;
}

const Header = (props: HeaderProps) => {
  const [show, setShow] = useState(false);
  const handleToggle = () => setShow(!show);
  const [env] = useState(getEnv());
  const { resolvedTheme, setTheme } = useTheme();

  const toggleTheme = () =>
    setTheme(resolvedTheme === "dark" ? "light" : "dark");

  return (
    <nav
      className={`flex flex-row justify-between text-foreground flex-nowrap mx-[10%] mt-8 mb-4 border-b border-border box-border leading-normal text-base text-left ${props.className || ""}`}
    >
      <div className="flex flex-nowrap items-center self-center justify-between w-full">
        <div className="flex items-center mr-5">
          <a href="/Endpoints" className="text-xl hover:no-underline">
            <img
              src="/nimbus_ascii_logo.png"
              className="h-12 w-auto max-w-none"
              alt="NimBus"
            />
          </a>
          <span className="hidden lg:inline ml-3 text-sm text-muted-foreground italic">
            A nimbus for your Azure cloud.
          </span>
          <Badge
            variant="secondary"
            className="ml-3 bg-transparent text-muted-foreground"
          >
            {env !== undefined ? env : getEnv()}
          </Badge>
        </div>

        <div className="block md:hidden cursor-pointer" onClick={handleToggle}>
          <svg
            fill="currentColor"
            width="32px"
            viewBox="0 0 24 24"
            xmlns="http://www.w3.org/2000/svg"
          >
            <title>Menu</title>
            <path d="M0 3h20v2H0V3zm0 6h20v2H0V9zm0 6h20v2H0v-2z" />
          </svg>
        </div>

        <div
          className={`${show ? "block" : "hidden"} md:flex items-center flex-grow w-full md:w-auto`}
        >
          <div className="flex">
            {props.links?.map((x) => (
              <MenuItem key={x.path}>
                {x.render ? (
                  <ReactLink
                    to={x.path}
                    className="text-foreground hover:text-primary"
                  >
                    {x.name}
                  </ReactLink>
                ) : (
                  <a
                    href={x.path}
                    className="text-foreground hover:text-primary"
                  >
                    {x.name}
                  </a>
                )}
              </MenuItem>
            ))}
          </div>
        </div>

        <button
          onClick={toggleTheme}
          className="p-2 rounded-md hover:bg-accent text-muted-foreground hover:text-accent-foreground transition-colors"
          aria-label="Toggle theme"
        >
          {resolvedTheme === "dark" ? (
            <svg
              xmlns="http://www.w3.org/2000/svg"
              width="20"
              height="20"
              viewBox="0 0 24 24"
              fill="none"
              stroke="currentColor"
              strokeWidth="2"
              strokeLinecap="round"
              strokeLinejoin="round"
            >
              <circle cx="12" cy="12" r="5" />
              <line x1="12" y1="1" x2="12" y2="3" />
              <line x1="12" y1="21" x2="12" y2="23" />
              <line x1="4.22" y1="4.22" x2="5.64" y2="5.64" />
              <line x1="18.36" y1="18.36" x2="19.78" y2="19.78" />
              <line x1="1" y1="12" x2="3" y2="12" />
              <line x1="21" y1="12" x2="23" y2="12" />
              <line x1="4.22" y1="19.78" x2="5.64" y2="18.36" />
              <line x1="18.36" y1="5.64" x2="19.78" y2="4.22" />
            </svg>
          ) : (
            <svg
              xmlns="http://www.w3.org/2000/svg"
              width="20"
              height="20"
              viewBox="0 0 24 24"
              fill="none"
              stroke="currentColor"
              strokeWidth="2"
              strokeLinecap="round"
              strokeLinejoin="round"
            >
              <path d="M21 12.79A9 9 0 1 1 11.21 3 7 7 0 0 0 21 12.79z" />
            </svg>
          )}
        </button>

        <div className={`${show ? "block" : "hidden"} md:block mt-4 md:mt-0`} />
      </div>
    </nav>
  );
};

export default Header;
