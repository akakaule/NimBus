import { useEffect, useState } from "react";
import moment from "moment";

export const useTime = (refreshCycle = 1000) => {
  const [now, setNow] = useState(getTime());

  useEffect(() => {
    const intervalId = setInterval(() => setNow(getTime()), refreshCycle);

    return () => clearInterval(intervalId);
  }, [refreshCycle, setInterval, clearInterval, setNow, getTime]);

  return now;
};

export const getTime = () => {
  return moment();
};
