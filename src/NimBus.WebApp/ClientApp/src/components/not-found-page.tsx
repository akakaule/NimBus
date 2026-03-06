import React from "react";
import Page from "./page";

interface INotFoundPage {
  errMsg: string;
}

export default function NotFoundPage(props: INotFoundPage) {
  return (
    <Page title={"Not Found"}>
      <>{props.errMsg}</>
    </Page>
  );
}
