import "./loading.css";

const Loading = (props: { diameter?: number }) => {
  const diameter = props.diameter || 100;

  return (
    <div
      className="loading relative inline-block"
      style={{ width: diameter, height: diameter }}
    >
      <div className="border-2 border-muted-foreground" />
      <div />
    </div>
  );
};

export default Loading;
