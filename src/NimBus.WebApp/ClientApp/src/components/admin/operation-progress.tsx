import { Badge } from "components/ui/badge";

interface OperationProgressProps {
  processed: number;
  succeeded: number;
  failed: number;
  errors?: string[];
  isComplete: boolean;
}

export default function OperationProgress({
  processed,
  succeeded,
  failed,
  errors = [],
  isComplete,
}: OperationProgressProps) {
  const total = processed;
  const progressPercent = total > 0 ? Math.round((succeeded / total) * 100) : 0;

  return (
    <div className="space-y-3">
      <div className="flex items-center gap-3">
        <div className="flex-1 bg-muted rounded-full h-2.5">
          <div
            className="bg-green-600 dark:bg-green-500 h-2.5 rounded-full transition-all duration-300"
            style={{ width: `${progressPercent}%` }}
          />
        </div>
        <span className="text-sm text-muted-foreground whitespace-nowrap">
          {succeeded}/{total}
        </span>
      </div>

      <div className="flex gap-3 text-sm">
        <span className="flex items-center gap-1">
          <Badge variant="success" size="sm">
            {succeeded}
          </Badge>
          succeeded
        </span>
        {failed > 0 && (
          <span className="flex items-center gap-1">
            <Badge variant="error" size="sm">
              {failed}
            </Badge>
            failed
          </span>
        )}
        {!isComplete && (
          <span className="text-muted-foreground italic">Processing...</span>
        )}
      </div>

      {errors.length > 0 && (
        <div className="mt-2 max-h-32 overflow-y-auto">
          <p className="text-xs font-medium text-red-700 dark:text-red-300 mb-1">Errors:</p>
          <ul className="text-xs text-red-600 dark:text-red-400 space-y-0.5">
            {errors.map((err, i) => (
              <li key={i} className="font-mono break-all">
                {err}
              </li>
            ))}
          </ul>
        </div>
      )}
    </div>
  );
}
