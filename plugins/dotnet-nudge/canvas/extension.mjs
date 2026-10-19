import { createCanvas, CanvasError, joinSession } from "@github/copilot-sdk/extension";
import { createDashboard } from "./provider.mjs";

let session;
const { canvas } = createDashboard({ createCanvas, CanvasError, getSession: () => session });
session = await joinSession({ canvases: [canvas] });
