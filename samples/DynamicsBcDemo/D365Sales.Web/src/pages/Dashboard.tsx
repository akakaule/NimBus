import { makeStyles, tokens, ToolbarButton, Tooltip } from '@fluentui/react-components';
import { ArrowClockwise20Regular } from '@fluentui/react-icons';
import { api, type Dashboard as DashboardData, type PipelineStage } from '../api';
import { CommandBar } from '../components/CommandBar';
import { ErrorState, LoadingState, StaleDataWarning } from '../components/PageStates';
import { Timeline } from '../components/Timeline';
import { Card, Section } from '../components/ui';
import { formatMoney } from '../format';
import { usePolling } from '../hooks/usePolling';
import { stageLabel } from '../model';

// Room kept at the end of each bar row for its value label, so the label never gets clipped.
const VALUE_LABEL_ROOM = 150;

const useStyles = makeStyles({
  heading: {
    display: 'flex',
    alignItems: 'baseline',
    gap: '12px',
    margin: '4px 4px 12px',
  },
  title: {
    margin: 0,
    fontSize: tokens.fontSizeBase500,
    lineHeight: tokens.lineHeightBase500,
    fontWeight: tokens.fontWeightSemibold,
  },
  subtitle: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
  tiles: {
    display: 'grid',
    gridTemplateColumns: 'repeat(3, minmax(0, 1fr))',
    gap: '12px',
    marginBottom: '12px',
  },
  tile: {
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
    padding: '16px 20px',
  },
  tileLabel: {
    fontSize: tokens.fontSizeBase300,
    color: tokens.colorNeutralForeground2,
  },
  tileValue: {
    fontSize: tokens.fontSizeHero800,
    lineHeight: tokens.lineHeightHero800,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground1,
  },
  tileDetail: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
  columns: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr) minmax(0, 1fr)',
    gap: '12px',
    alignItems: 'start',
    '@media (max-width: 1100px)': {
      gridTemplateColumns: 'minmax(0, 1fr)',
    },
  },
  bars: {
    listStyleType: 'none',
    margin: 0,
    padding: '4px 0 0',
    display: 'flex',
    flexDirection: 'column',
    gap: '14px',
  },
  barRow: {
    display: 'grid',
    gridTemplateColumns: '80px minmax(0, 1fr)',
    alignItems: 'center',
    columnGap: '12px',
  },
  barLabel: {
    fontSize: tokens.fontSizeBase300,
    color: tokens.colorNeutralForeground2,
  },
  barArea: {
    display: 'flex',
    alignItems: 'center',
    gap: '10px',
    minHeight: '28px',
    borderLeft: `1px solid ${tokens.colorNeutralStroke1}`,
  },
  bar: {
    height: '20px',
    borderRadius: `0 ${tokens.borderRadiusMedium} ${tokens.borderRadiusMedium} 0`,
    backgroundColor: tokens.colorBrandBackground,
    flexShrink: 0,
    transitionProperty: 'width',
    transitionDuration: tokens.durationSlow,
    ':focus-visible': {
      outlineStyle: 'solid',
      outlineWidth: '2px',
      outlineOffset: '2px',
      outlineColor: tokens.colorStrokeFocus2,
    },
  },
  barValue: {
    fontSize: tokens.fontSizeBase300,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground1,
    whiteSpace: 'nowrap',
  },
  barCount: {
    fontWeight: tokens.fontWeightRegular,
    color: tokens.colorNeutralForeground3,
  },
  activity: {
    maxHeight: '460px',
    overflowY: 'auto',
  },
});

const plural = (count: number, one: string, many: string) => `${count} ${count === 1 ? one : many}`;

function StatTile({ label, value, detail, testId }: { label: string; value: string; detail: string; testId: string }) {
  const styles = useStyles();
  return (
    <Card className={styles.tile} testId={testId}>
      <span className={styles.tileLabel}>{label}</span>
      <span className={styles.tileValue}>{value}</span>
      <span className={styles.tileDetail}>{detail}</span>
    </Card>
  );
}

function PipelineBars({ stages }: { stages: PipelineStage[] }) {
  const styles = useStyles();
  const max = Math.max(0, ...stages.map((s) => s.value));
  return (
    <ul className={styles.bars} aria-label="Open pipeline by stage">
      {stages.map((s) => {
        const ratio = max > 0 ? s.value / max : 0;
        const label = `${stageLabel(s.stage)}: ${formatMoney(s.value)} across ${plural(s.count, 'open opportunity', 'open opportunities')}`;
        return (
          <li key={s.stage} className={styles.barRow}>
            <span className={styles.barLabel}>{stageLabel(s.stage)}</span>
            <div className={styles.barArea}>
              {ratio > 0 && (
                <Tooltip content={label} relationship="description" positioning="above-start">
                  <div
                    className={styles.bar}
                    style={{ width: `calc((100% - ${VALUE_LABEL_ROOM}px) * ${ratio.toFixed(4)})` }}
                    tabIndex={0}
                    role="img"
                    aria-label={label}
                  />
                </Tooltip>
              )}
              <span className={styles.barValue}>
                {formatMoney(s.value)}
                <span className={styles.barCount}> · {s.count}</span>
              </span>
            </div>
          </li>
        );
      })}
    </ul>
  );
}

function DashboardBody({ data }: { data: DashboardData }) {
  const styles = useStyles();
  return (
    <>
      <div className={styles.tiles}>
        <StatTile
          label="Open pipeline"
          value={formatMoney(data.openValue)}
          detail={plural(data.openCount, 'open opportunity', 'open opportunities')}
          testId="tile-open-pipeline"
        />
        <StatTile
          label="Won"
          value={formatMoney(data.wonValue)}
          detail={
            data.wonCount === 1
              ? '1 opportunity, won from a Business Central order'
              : `${data.wonCount} opportunities, won from Business Central orders`
          }
          testId="tile-won"
        />
        <StatTile
          label="Quotes in Business Central"
          value={String(data.quotesInBc)}
          detail="open opportunities with a Business Central quote"
          testId="tile-quotes-in-bc"
        />
      </div>
      <div className={styles.columns}>
        <Section title="Pipeline by stage" caption="Estimated revenue of open opportunities · count">
          <PipelineBars stages={data.pipeline} />
        </Section>
        <Section title="Integration activity" caption="Latest updates from Business Central, newest first">
          <div className={styles.activity}>
            <Timeline
              entries={data.integrationActivity}
              emptyText="No integration activity yet"
              testId="integration-activity"
            />
          </div>
        </Section>
      </div>
    </>
  );
}

export function Dashboard() {
  const styles = useStyles();
  const poll = usePolling('dashboard', (signal) => api.dashboard(signal), 3000);

  return (
    <>
      <CommandBar label="Dashboard commands">
        <ToolbarButton icon={<ArrowClockwise20Regular />} onClick={() => void poll.refresh()}>
          Refresh
        </ToolbarButton>
      </CommandBar>
      <div className={styles.heading}>
        <h1 className={styles.title}>Sales dashboard</h1>
        <span className={styles.subtitle}>Contoso Subsea · updates live</span>
      </div>
      {poll.loading ? (
        <LoadingState label="Loading the dashboard…" />
      ) : !poll.data ? (
        <ErrorState title="The dashboard could not be loaded" message={poll.error ?? ''} onRetry={() => void poll.refresh()} />
      ) : (
        <>
          <StaleDataWarning error={poll.error} />
          <DashboardBody data={poll.data} />
        </>
      )}
    </>
  );
}
