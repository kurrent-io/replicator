import { createApp } from "vue";
import App from "./App.vue";
import router from "./router";
import {store, key} from "./store";
// @ts-ignore
import installElementPlus from "./plugins/element.js";

const app = createApp(App);
installElementPlus(app);
app.use(store, key).use(router).mount("#app");
