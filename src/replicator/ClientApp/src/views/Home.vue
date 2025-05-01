<template>
    <div class="home">
        <Dashboard/>
    </div>
</template>

<script lang="ts">
import {defineComponent} from "vue";
import Dashboard from "@/components/Dashboard.vue";

export default defineComponent({
    name: "Home",
    components: {
        Dashboard,
    },
});
</script>
