import { useRef, useEffect } from "react";
import { useNavigate } from "react-router-dom";
import { Button } from "components/ui/button";

interface IPage {
  title: string;
  offsetDimensionsHandler?: (height: number, width: number) => void;
  children: React.ReactNode;
  backbutton?: boolean;
  backUrl?: string;
  backIndex?: string;
}

// Arrow back icon SVG
const ArrowBackIcon = () => (
  <svg
    xmlns="http://www.w3.org/2000/svg"
    viewBox="0 0 24 24"
    fill="currentColor"
    className="w-8 h-8"
  >
    <path d="M20 11H7.83l5.59-5.59L12 4l-8 8 8 8 1.41-1.41L7.83 13H20v-2z" />
  </svg>
);

const Page: React.FC<IPage> = (props) => {
  const ref = useRef<HTMLDivElement>(null);
  const navigate = useNavigate();

  useEffect(() => {
    if (ref.current && props.offsetDimensionsHandler !== undefined) {
      props.offsetDimensionsHandler(
        ref.current.offsetWidth,
        ref.current.offsetHeight,
      );
    }
  }, [ref.current, ref.current?.clientHeight]);

  return (
    <div className="flex flex-col mx-[10%] min-h-0 flex-1">
      <h1 className="text-3xl font-bold my-8 flex items-center gap-2">
        {props.backbutton ? (
          <Button
            onClick={() => {
              navigate(props.backUrl!, {
                state: { tabIndex: props.backIndex },
              });
            }}
            aria-label="Go back"
            variant="ghost"
            size="sm"
            className="p-0"
          >
            <ArrowBackIcon />
          </Button>
        ) : null}
        {props.title}
      </h1>
      <div ref={ref} className="flex min-h-0 flex-1">
        {props.children}
      </div>
    </div>
  );
};

export default Page;
