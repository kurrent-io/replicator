<template>
  <el-card shadow="never">
    <el-progress :percentage="percentage" />
    <span class="card-value">{{ size }} / {{ capacity}}</span>
    <span class="card-label">{{ name }}</span>
  </el-card>
</template>

<script lang="ts">
import {computed} from "vue";

export default {
    name: "ChannelSize",
    props: {
        name: String,
        capacity: Number,
        size: Number
    },
    setup(props: any) {
        const percentage = computed(() => Math.round(props.size / props.capacity * 100));
        return {
            percentage: percentage
        }
    }
}
</script>

<style scoped lang="stylus">
.card-label
  display block
  text-align center
  margin-top 10px
  font-size 10px
.card-value
  display block
  text-align center
  margin-top 10px
  font-size 16px
</style>
