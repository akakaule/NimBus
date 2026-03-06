import * as React from "react";
import Tree from "react-d3-tree";
import type { RawNodeDatum } from "react-d3-tree";
import "./event-tree.css";

export enum View {
  Produces,
  Consumes,
}

interface IEventTreeProps {
  data: RawNodeDatum;
  view: View;
  pageDimensions: { width: number; height: number };
  translate: { x: number; y: number };
}

export function EventTree(props: IEventTreeProps) {
  return (
    <div
      id="treeWrapper"
      style={{ width: "100%", height: props.pageDimensions.height * 0.66 }}
      className="tree-container"
    >
      <Tree
        rootNodeClassName={
          props.view === View.Consumes
            ? "node__root__consume"
            : "node__root__produce"
        }
        branchNodeClassName={
          props.view === View.Consumes
            ? "node__branch__consume"
            : "node__branch__produce"
        }
        leafNodeClassName={
          props.view === View.Consumes
            ? "node__leaf__consume"
            : "node__leaf__produce"
        }
        depthFactor={300}
        initialDepth={300}
        nodeSize={{ x: 250, y: 250 }}
        zoom={0.7}
        translate={props.translate}
        collapsible={true}
        data={props.data}
        orientation="vertical"
      />
    </div>
  );
}
